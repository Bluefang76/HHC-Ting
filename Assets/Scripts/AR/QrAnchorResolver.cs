using System;
using System.Collections;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Wayfinding.Data;

namespace Wayfinding.AR
{
    /// <summary>
    /// Reads the QR code posted at the entrance and turns it into a known place and, crucially,
    /// a known FACING.
    ///
    /// It is worth being precise about why this exists, because the QR code looks like the least
    /// technical part of the idea and is actually load-bearing. Beacons can tell you WHERE you
    /// are. Nothing in the beacon stack can tell you which way you are POINTED — trilateration
    /// gives a point, not a direction, and a point has no orientation. Heading could come from
    /// the phone's compass, except that a hospital corridor is a steel-framed building full of
    /// motors and magnets, and indoor compass readings there are routinely 30 to 90 degrees wrong.
    /// If the heading is wrong, the racing line is drawn down the wrong side of the hallway and
    /// the whole illusion collapses, no matter how good the position is.
    ///
    /// So: post the code where there is exactly one comfortable way to stand and scan it. That
    /// scan gives position and heading simultaneously, AR tracking carries the heading from
    /// there, and the beacons keep the position honest as the visitor walks.
    ///
    /// DECODING: AR Foundation 6 has no cross-platform QR decoder. The usual answer is ZXing.Net.
    /// Import it, add WAYFINDING_ZXING to your scripting defines, and fill in the one method in
    /// the DECODER BRIDGE region. Until then <see cref="ResolveCode"/> still works, so you can
    /// drive the whole flow from a debug button and build the rest of the app.
    /// </summary>
    public class QrAnchorResolver : MonoBehaviour
    {
        [Header("Dependencies")]
        public FloorMap floorMap;

        [Tooltip("ARCameraManager from your XR Origin. Supplies the CPU image to decode.")]
        public ARCameraManager cameraManager;

        [Header("Scanning")]
        [Tooltip("Decode attempts per second. Decoding is not free and the camera delivers far " +
                 "more frames than needed. 4 finds a code near-instantly in normal use.")]
        [Range(1f, 15f)]
        public float decodeRate = 4f;

        [Tooltip("Stop scanning once a code has been resolved. Turn off for the survey workflow, " +
                 "where re-scanning a code is how you reset a drifted session.")]
        public bool stopAfterFirstResolve = true;

        [Header("Debug")]
        [Tooltip("Resolve this code automatically a moment after start, with no camera involved. " +
                 "Set it to one of your FloorMap's QR codes to test the whole flow in the editor.")]
        public string editorAutoResolveCode = "";

        public bool verboseLogging;

        /// <summary>Fires when a scanned code matches a QR anchor in the FloorMap.</summary>
        public event Action<FloorMap.QrAnchor> AnchorResolved;

        /// <summary>Fires when a code decodes but is not one of ours — likely a poster or a lab label.</summary>
        public event Action<string> UnknownCodeScanned;

        /// <summary>True once a valid anchor has been resolved this session.</summary>
        public bool HasResolved { get; private set; }

        /// <summary>The anchor most recently resolved. Null until then.</summary>
        public FloorMap.QrAnchor LastAnchor { get; private set; }

        private float _nextDecodeTime;
        private bool _decoding;

        private void OnEnable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived += OnCameraFrameReceived;
            }

#if UNITY_EDITOR
            if (!string.IsNullOrEmpty(editorAutoResolveCode))
            {
                StartCoroutine(AutoResolveInEditor());
            }
#endif
        }

        private void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= OnCameraFrameReceived;
            }
        }

#if UNITY_EDITOR
        private IEnumerator AutoResolveInEditor()
        {
            // A beat, so anything listening has finished subscribing.
            yield return new WaitForSeconds(0.5f);
            ResolveCode(editorAutoResolveCode);
        }
#endif

        /// <summary>
        /// Handles a decoded string. Public so a debug button, a typed code, or a future NFC tap
        /// can feed the same path as the camera.
        /// </summary>
        public bool ResolveCode(string code)
        {
            if (floorMap == null || string.IsNullOrWhiteSpace(code))
            {
                return false;
            }

            FloorMap.QrAnchor anchor = floorMap.FindQrAnchor(code.Trim());

            if (anchor == null)
            {
                if (verboseLogging)
                {
                    Debug.Log($"[QrAnchorResolver] Scanned an unrecognised code: '{code}'.");
                }

                UnknownCodeScanned?.Invoke(code);
                return false;
            }

            HasResolved = true;
            LastAnchor = anchor;

            Debug.Log($"[QrAnchorResolver] Resolved '{code}' -> {anchor.label} at " +
                      $"{anchor.position}, heading {anchor.headingDegrees:F0} deg.");

            AnchorResolved?.Invoke(anchor);
            return true;
        }

        /// <summary>Allows a rescan after a resolve, e.g. from a "reset position" button.</summary>
        public void AllowRescan()
        {
            HasResolved = false;
        }

        private void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (_decoding || floorMap == null)
            {
                return;
            }

            if (HasResolved && stopAfterFirstResolve)
            {
                return;
            }

            if (Time.unscaledTime < _nextDecodeTime)
            {
                return;
            }

            _nextDecodeTime = Time.unscaledTime + (1f / Mathf.Max(decodeRate, 1f));
            TryDecodeLatestFrame();
        }

        private unsafe void TryDecodeLatestFrame()
        {
            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
            {
                return;
            }

            _decoding = true;

            try
            {
                // Convert to 8-bit greyscale at reduced size. QR decoding only needs luminance,
                // and a quarter-size image decodes several times faster with no practical loss —
                // which matters when this runs on every fourth camera frame.
                var conversionParams = new XRCpuImage.ConversionParams
                {
                    inputRect = new RectInt(0, 0, image.width, image.height),
                    outputDimensions = new Vector2Int(image.width / 2, image.height / 2),
                    outputFormat = TextureFormat.R8,
                    transformation = XRCpuImage.Transformation.None
                };

                int size = image.GetConvertedDataSize(conversionParams);
                var buffer = new NativeArray<byte>(size, Allocator.Temp);

                try
                {
                    image.Convert(conversionParams, new IntPtr(buffer.GetUnsafePtr()), buffer.Length);

                    string decoded = DecodeLuminance(
                        buffer,
                        conversionParams.outputDimensions.x,
                        conversionParams.outputDimensions.y);

                    if (!string.IsNullOrEmpty(decoded))
                    {
                        ResolveCode(decoded);
                    }
                }
                finally
                {
                    buffer.Dispose();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[QrAnchorResolver] Decode failed: {exception.Message}");
            }
            finally
            {
                image.Dispose();
                _decoding = false;
            }
        }

        // ==================================================================
        // DECODER BRIDGE
        // The only place a QR library is referenced.
        // ==================================================================

        /// <summary>
        /// Decodes a QR code from an 8-bit greyscale buffer. Returns null when there is no code
        /// in frame, which is most frames — that is normal, not an error.
        /// </summary>
        private string DecodeLuminance(NativeArray<byte> luminance, int width, int height)
        {
#if WAYFINDING_ZXING
            // ---- FILL IN ONCE ZXing.Net IS IMPORTED -----------------------
            // Keep the reader as a cached field rather than constructing one per frame — it
            // allocates, and this runs several times a second.
            //
            //   private ZXing.IBarcodeReader _reader;
            //
            //   _reader ??= new ZXing.BarcodeReader
            //   {
            //       AutoRotate = true,
            //       Options = new ZXing.Common.DecodingOptions
            //       {
            //           PossibleFormats = new[] { ZXing.BarcodeFormat.QR_CODE },
            //           TryHarder = false   // false keeps it fast; the code is a metre away and well lit
            //       }
            //   };
            //
            //   var source = new ZXing.RGBLuminanceSource(
            //       luminance.ToArray(), width, height, ZXing.RGBLuminanceSource.BitmapFormat.Gray8);
            //
            //   return _reader.Decode(source)?.Text;
            return null;
#else
            return null;
#endif
        }
    }
}
