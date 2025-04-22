using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace MuOnlineConsole.GUI.ViewModels
{
    /// <summary>
    /// Defines the type of object displayed on the map.
    /// </summary>
    public enum MapObjectType { PlayerSelf, PlayerOther, NpcMerchant, NpcGuard, NpcQuest, MonsterNormal, MonsterBoss, Item, Money, Unknown }

    /// <summary>
    /// View model for a single object displayed on the map.
    /// </summary>
    public partial class MapObjectViewModel : ObservableObject
    {
        // Observable properties for UI binding
        [ObservableProperty] private double _mapX;
        [ObservableProperty] private double _mapY;
        [ObservableProperty] private double _size = 5;
        [ObservableProperty] private IBrush _color = Brushes.Gray;
        [ObservableProperty] private string? _toolTipText;

        // Properties initialized once
        public ushort Id { get; init; }
        public ushort RawId { get; init; }
        public MapObjectType ObjectType { get; init; }

        // Original game position
        public byte OriginalX { get; private set; }
        public byte OriginalY { get; private set; }

        // Maximum map coordinate (assuming 255)
        private const byte MaxMapCoordinate = 255;

        /// <summary>
        /// Initializes a new instance of the <see cref="MapObjectViewModel"/> class.
        /// </summary>
        /// <param name="id">The object's unique ID.</param>
        /// <param name="rawId">The object's raw ID.</param>
        /// <param name="objectType">The type of map object.</param>
        /// <param name="initialX">The initial X coordinate in game units.</param>
        /// <param name="initialY">The initial Y coordinate in game units.</param>
        public MapObjectViewModel(ushort id, ushort rawId, MapObjectType objectType, byte initialX, byte initialY)
        {
            Id = id;
            RawId = rawId;
            ObjectType = objectType;
            OriginalX = initialX;
            OriginalY = initialY;
        }

        /// <summary>
        /// Updates the position of the map object based on new game coordinates and the current map scale.
        /// Calculates the MapY by inverting the Y coordinate.
        /// </summary>
        /// <param name="x">The new X coordinate in game units.</param>
        /// <param name="y">The new Y coordinate in game units.</param>
        /// <param name="scale">The current map scale.</param>
        public void UpdatePosition(byte x, byte y, double scale)
        {
            OriginalX = x;
            OriginalY = y;

            double newMapX = x * scale;
            double newMapY = (MaxMapCoordinate - y) * scale;

            // Console.WriteLine($"[MapObj UpdatePosition] ID {Id:X4}: Input Pos=({x},{y}), Scale={scale:F2}. Calculated MapPos=({newMapX:F2},{newMapY:F2})");

            MapX = newMapX;
            MapY = newMapY;

            // Update tooltip text while keeping the prefix
            if (!string.IsNullOrEmpty(ToolTipText))
            {
                var parts = ToolTipText.Split('@');
                ToolTipText = $"{parts[0].Trim()} @ ({x},{y})";
            }
            else
            {
                // Fallback if ToolTipText was not set initially (should not happen with current logic)
                ToolTipText = $"Unknown Object ID={Id:X4} @ ({x},{y})";
            }
        }

        /// <summary>
        /// Updates the scale of the map object based on the new map scale.
        /// Calculates the MapX and MapY based on the stored original position and the new scale.
        /// </summary>
        /// <param name="newScale">The new map scale.</param>
        public void UpdateScale(double newScale)
        {
            double newMapX = OriginalX * newScale;
            double newMapY = (MaxMapCoordinate - OriginalY) * newScale; // Invert Y

            // Console.WriteLine($"[MapObj UpdateScale] ID {Id:X4}: OriginalPos=({OriginalX},{OriginalY}), NewScale={newScale:F2}. Calculated MapPos=({newMapX:F2},{newMapY:F2})");

            MapX = newMapX;
            MapY = newMapY;
        }
    }
}