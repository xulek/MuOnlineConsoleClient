using Avalonia.Media; // Dla Brush
using CommunityToolkit.Mvvm.ComponentModel;

namespace MuOnlineConsole.GUI.ViewModels
{
    // Typy obiektów dla mapy (może być bardziej szczegółowe niż ScopeObjectType)
    public enum MapObjectType { PlayerSelf, PlayerOther, NpcMerchant, NpcGuard, NpcQuest, MonsterNormal, MonsterBoss, Item, Money, Unknown }

    public partial class MapObjectViewModel : ObservableObject
    {
        // ... Observable properties for MapX, MapY, Size, Color, ToolTipText ...
        [ObservableProperty] private double _mapX;
        [ObservableProperty] private double _mapY;
        [ObservableProperty] private double _size = 5;
        [ObservableProperty] private IBrush _color = Brushes.Gray;
        [ObservableProperty] private string? _toolTipText;

        // --- Init-only properties ---
        public ushort Id { get; init; }
        public ushort RawId { get; init; }
        public MapObjectType ObjectType { get; init; }

        // --- Properties for Original Position - Keep 'private set' ---
        public byte OriginalX { get; private set; }
        public byte OriginalY { get; private set; }
        // --- End of change ---


        // --- ADD CONSTRUCTOR ---
        public MapObjectViewModel(ushort id, ushort rawId, MapObjectType objectType, byte initialX, byte initialY)
        {
            Id = id;
            RawId = rawId;
            ObjectType = objectType;
            OriginalX = initialX; // Set via constructor
            OriginalY = initialY; // Set via constructor
        }
        // --- END OF CONSTRUCTOR ---

        // UpdatePosition CAN NOW set OriginalX/Y because it's inside the class
        public void UpdatePosition(byte x, byte y, double scale)
        {
            byte oldX = OriginalX;
            byte oldY = OriginalY;
            double oldMapX = MapX;
            double oldMapY = MapY;

            // This assignment IS VALID because it's inside MapObjectViewModel
            OriginalX = x;
            OriginalY = y;

            double newMapX = x * scale;
            double newMapY = y * scale;

            Console.WriteLine($"[MapObj UpdatePosition] ID {Id:X4}: Input Pos=({x},{y}), Scale={scale:F2}. Calculated MapPos=({newMapX:F2},{newMapY:F2}). Old OriginalPos=({oldX},{oldY}), Old MapPos=({oldMapX:F2},{oldMapY:F2})");

            MapX = newMapX;
            MapY = newMapY;
            ToolTipText = $"{ToolTipText?.Split('@')[0].Trim()} @ ({x},{y})";
        }

        public void UpdateScale(double newScale)
        {
            double oldMapX = MapX;
            double oldMapY = MapY;
            double newMapX = OriginalX * newScale; // Uses the current OriginalX
            double newMapY = OriginalY * newScale; // Uses the current OriginalY

            Console.WriteLine($"[MapObj UpdateScale] ID {Id:X4}: OriginalPos=({OriginalX},{OriginalY}), NewScale={newScale:F2}. Calculated MapPos=({newMapX:F2},{newMapY:F2}). Old MapPos=({oldMapX:F2},{oldMapY:F2})");

            MapX = newMapX;
            MapY = newMapY;
        }
    }
}