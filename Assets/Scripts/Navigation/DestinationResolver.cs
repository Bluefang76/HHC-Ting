using UnityEngine;
using Wayfinder.Mapping;

namespace Wayfinder.Navigation
{
    /// <summary>
    /// Turns what a visitor types — or what is printed on their appointment slip — into
    /// a position on the floor.
    ///
    /// This is where the messiness of real hospital room numbering gets absorbed, so it
    /// does not leak into pathing or UI.
    /// </summary>
    public sealed class DestinationResolver
    {
        private readonly FloorMap _floorMap;

        public DestinationResolver(FloorMap floorMap)
        {
            _floorMap = floorMap;
        }

        public enum Result
        {
            Found,
            NotFound,
            Ambiguous,        // e.g. the number exists in more than one building
            WrongFloor        // exists, but not on the loaded floor
        }

        /// <summary>
        /// Resolve a typed room number to a doorway position in map coordinates.
        /// </summary>
        public Result TryResolve(string query, out Vector2 mapPosition, out string resolvedRoomNumber)
        {
            mapPosition = default;
            resolvedRoomNumber = null;

            if (_floorMap == null || string.IsNullOrWhiteSpace(query)) return Result.NotFound;

            if (_floorMap.TryFindRoom(query, out var room))
            {
                mapPosition = room.doorPosition;
                resolvedRoomNumber = room.roomNumber;
                return Result.Found;
            }

            // TODO: handle suffixed bays (214A -> 214), near-miss suggestions for typos,
            //       and the WrongFloor / Ambiguous cases once more than one floor is mapped.
            return Result.NotFound;
        }
    }
}
