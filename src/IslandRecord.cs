using System;
using TaleWorlds.Library;

namespace BLWarsailsInhabitableIslands
{
    internal sealed class IslandRecord
    {
        public IslandRecord(
            string id,
            string zoneName,
            Vec2 center,
            Vec2 axis,
            Vec2 perpendicular,
            float length,
            float width,
            float height,
            int sampleCount)
        {
            Id = id;
            ZoneName = zoneName;
            Center = center;
            Axis = axis;
            Perpendicular = perpendicular;
            Length = length;
            Width = width;
            Height = height;
            SampleCount = sampleCount;
        }

        public string Id { get; }
        public string ZoneName { get; }
        public Vec2 Center { get; }
        public Vec2 Axis { get; }
        public Vec2 Perpendicular { get; }
        public float Length { get; }
        public float Width { get; }
        public float Height { get; }
        public int SampleCount { get; }
        public float Area => Length * Width;

        public bool Contains(Vec2 position, float margin = 0.25f)
        {
            Vec2 delta = position - Center;
            float along = Math.Abs(Dot(delta, Axis));
            float across = Math.Abs(Dot(delta, Perpendicular));
            return along <= Length * 0.5f + margin &&
                   across <= Width * 0.5f + margin;
        }

        public Vec2 ClampInside(Vec2 position, float inset)
        {
            float halfLength = Math.Max(0.1f, Length * 0.5f - inset);
            float halfWidth = Math.Max(0.1f, Width * 0.5f - inset);
            Vec2 delta = position - Center;
            float along = Clamp(Dot(delta, Axis), -halfLength, halfLength);
            float across = Clamp(Dot(delta, Perpendicular), -halfWidth, halfWidth);
            return Center + Axis * along + Perpendicular * across;
        }

        public Vec2 GetEdgePoint(Vec2 direction, float insetFraction = 0.48f)
        {
            float directionLength = (float)Math.Sqrt(
                direction.X * direction.X +
                direction.Y * direction.Y);
            if (directionLength < 0.001f)
            {
                direction = Axis;
            }
            else
            {
                direction /= directionLength;
            }

            float along = Math.Abs(Dot(direction, Axis));
            float across = Math.Abs(Dot(direction, Perpendicular));
            float alongDistance = along < 0.001f
                ? float.MaxValue
                : Length * insetFraction / along;
            float acrossDistance = across < 0.001f
                ? float.MaxValue
                : Width * insetFraction / across;
            return Center + direction * Math.Min(alongDistance, acrossDistance);
        }

        private static float Dot(Vec2 left, Vec2 right)
        {
            return left.X * right.X + left.Y * right.Y;
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
