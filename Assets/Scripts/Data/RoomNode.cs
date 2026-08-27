using System;
using UnityEngine;

namespace Wayfinding.Data
{
    /// <summary>
    /// One destination a visitor can type in. Rooms are the only thing a user searches for,
    /// so the room number is the primary key and everything else is presentation.
    /// </summary>
    [Serializable]
    public class RoomNode
    {
        [Header("Identity")]
        [Tooltip("What the visitor types. Keep it exactly as it appears on the physical door plate, " +
                 "including any letter suffix: '412', '412A', 'B-17'.")]
        public string roomNumber = "";

        [Tooltip("Shown under the room number in the UI, e.g. 'Cardiology - Exam 3'. Optional.")]
        public string displayName = "";

        [Tooltip("Used for grouping and for search-by-department later. Optional.")]
        public string department = "";

        [Header("Placement (floor space, metres)")]
        [Tooltip("The centre of the doorway itself. This is what the arrival pin points at.")]
        public Vector2 doorPosition;

        [Tooltip("The point in the hallway directly outside the door — where a person actually " +
                 "stands when they have arrived. The path routes HERE, not to the door, because " +
                 "the door itself sits in a wall and is not walkable.")]
        public Vector2 approachPosition;

        [Header("Search")]
        [Tooltip("Extra terms that should match this room in search: old room numbers, nicknames " +
                 "staff use, the name of the clinic that used to be here.")]
        public string[] searchAliases = Array.Empty<string>();

        [Tooltip("Uncheck for rooms visitors should never be routed to (staff-only, storage). " +
                 "They stay in the map for completeness but drop out of search.")]
        public bool publiclyRoutable = true;

        /// <summary>
        /// Case- and whitespace-insensitive match against the room number and every alias.
        /// Also tolerates the dashes and spaces people type inconsistently ("B-17" vs "b17").
        /// </summary>
        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string needle = Normalize(query);

            if (Normalize(roomNumber) == needle)
            {
                return true;
            }

            if (searchAliases != null)
            {
                for (int i = 0; i < searchAliases.Length; i++)
                {
                    if (Normalize(searchAliases[i]) == needle)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Looser match used for the "did you mean" suggestion list as the user types.
        /// </summary>
        public bool StartsWith(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return false;
            }

            string needle = Normalize(query);
            return needle.Length > 0 && Normalize(roomNumber).StartsWith(needle, StringComparison.Ordinal);
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var buffer = new System.Text.StringBuilder(value.Length);

            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    buffer.Append(char.ToUpperInvariant(c));
                }
            }

            return buffer.ToString();
        }

        /// <summary>Label for the confirmation screen: "412 - Cardiology Exam 3".</summary>
        public string FullLabel =>
            string.IsNullOrEmpty(displayName) ? roomNumber : $"{roomNumber} - {displayName}";
    }
}
