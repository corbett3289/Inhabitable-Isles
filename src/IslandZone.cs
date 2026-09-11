using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal readonly struct IslandZone
    {
        public IslandZone(string name, float minX, float maxX, float minY, float maxY)
        {
            Name = name;
            MinX = minX;
            MaxX = maxX;
            MinY = minY;
            MaxY = maxY;
        }

        public string Name { get; }
        public float MinX { get; }
        public float MaxX { get; }
        public float MinY { get; }
        public float MaxY { get; }

        public bool Contains(Vec2 position)
        {
            return position.X >= MinX &&
                   position.X <= MaxX &&
                   position.Y >= MinY &&
                   position.Y <= MaxY;
        }

        public override string ToString()
        {
            return string.Format("{0} [{1:0.##},{2:0.##}]x[{3:0.##},{4:0.##}]", Name, MinX, MaxX, MinY, MaxY);
        }
    }
}
