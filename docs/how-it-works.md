# How the Wayfinder Works

*Hospital indoor wayfinding · pilot floor*

Three views of the same app: what a visitor experiences, what the code is doing, and the
two laid over each other.

> Ported from the "How the Wayfinder Works" artifact. The original is a set of diagrams;
> this is the same content as text so it is greppable from the repo.

---

## 1 · The visitor's journey — *no code*

Six steps, and the five places a step can fail. Every failure has somewhere to go — a
visitor should never reach a dead end in a lobby.

### The path that works

| # | Step | |
|---|---|---|
| 1 | **Walks in** | at the main entrance |
| 2 | **Scans the code** | posted by the door |
| 3 | **Types the room** | from the appointment letter |
| 4 | **Confirms** | "about 60 m, 1 minute" |
| 5 | **Follows the line** | through the phone camera |
| 6 | **Arrives** | within 3.5 m of the door |

### When a step can't complete

| At step | Condition | What the app does |
|---|---|---|
| 2 | Bluetooth is off | Asks them to switch it on, and says it collects nothing |
| 2 | Not our code | Points them back to the wayfinding code by the door |
| 3 | No such room | Suggests checking the letter, or asking at the front desk |
| 5 | Position uncertain | "Finding your position — keep walking". **No arrow shown.** |
| 5 | Wandered off | More than 4 m off for 2 s, then re-routes silently |

Every one of these returns to the step it came from — nothing here terminates. The
"position uncertain" branch is the important one: the app stops showing a direction
rather than showing a confident wrong one, because a visitor forgives three seconds of
"finding you" and does not forgive being sent down the wrong corridor.

---

## 2 · The system — *every script*

Four stages. Each one takes something messy and hands the next one something clean —
which is why nothing downstream of `BeaconManager` has ever heard the word RSSI.

```
  SENSE                 LOCATE                  ROUTE                    RENDER
  ─────                 ──────                  ─────                    ──────

30 × BC011 beacons ─┐
transmit only       │   RssiFilter × 30
1–2 Hz each         ├─> median → one-euro     FloorGeometryBuilder    ARPathRenderer
                    │   → metres              corridors → triangles   ribbon, rebuilt
BleBeaconScanner ───┘        │                        │               every 0.25 m
the only file that           v                        v                     ^
knows the plugin        Trilateration              NavMesh              ARWorldAligner
                        Gauss-Newton,           baked once, at         align once,
                        3+ anchors               startup               then 15 cm/s
                             │                        │                     ^
                             v                        v                     │
                       BeaconManager ──────>  PathfindingEngine             │
                       solves 8× a second     snap · smooth ·               │
                       → PositionFix          resample 0.5 m                │
                       speed limit + snap             │                     │
                       to corridor applied            v                     │
                       before publishing       NavigationSession            │
                             │                 re-routes every 3 s,         │
                             │                 not every fix                │
                             │                        │                     │
                             │                        v                     │
                             │                  UIController                │
                             │                  "Turn left in 8 m"          │
                             │                                              │
                             └──── position, for drift correction ──────────┘

Entrance QR code ──> QrAnchorResolver ──── QR pose: position and heading, ───┘
a sticker on a wall  decodes → position     once at the start
                     and heading

                              ↓
                     THE RACING LINE on the real floor

FloorMap — the survey
beacon positions and TX power · corridor geometry · room list · QR anchor poses
every stage reads its constants from this one asset
```

The two arcs are the pieces that are easy to miss:

- **Heading comes only from the QR scan.** Trilateration produces a point, and a point
  has no orientation. Without that arc the line is drawn down the wrong side of the
  corridor no matter how good the position is.
- **The beacons correct AR's drift continuously, at 15 cm/s** — slow enough that nobody
  sees it happen, fast enough to outrun the drift.

---

## 3 · Both at once — *one page*

What fires when. Useful for debugging: when something goes wrong at a particular moment
in the walk, this narrows it to two or three files.

| Step | Duration | What runs |
|---|---|---|
| 1 · Walks in | app not open yet | nothing yet |
| 2 · Scans the code | one second | `QrAnchorResolver`, `ARWorldAligner`, `BeaconManager` — position + heading fixed |
| 3 · Types the room | "412" | `FloorMap.FindRoom`, `UIController` — suggestions as they type |
| 4 · Confirms | route computed once | `NavigationSession`, `PathfindingEngine` — first path computed |
| 5 · Follows the line | **the whole walk — loops until arrival** | `BeaconManager` · 8 Hz · `NavigationSession` · every 3 s · `ARWorldAligner` · drift · `ARPathRenderer` · 0.25 m |
| 6 · Arrives | within 3.5 m | `NavigationSession`, `UIController` — arrival, path cleared |

`FloorMap` is read at every step above.
`FloorGeometryBuilder` + NavMesh bake run **once, at app startup**, before any of this.

Note how little runs at most steps, and how much runs during the walk. **Step 5 is the
whole engineering problem** — four systems on four different clocks, all needing to agree
about where a person is. Steps 2, 3, 4 and 6 are each a handful of function calls.

---

*Hospital indoor wayfinding · one floor, one pilot · Unity 6 + AR Foundation 6.x + BLE trilateration*
