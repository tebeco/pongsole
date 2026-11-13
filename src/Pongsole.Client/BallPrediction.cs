using System.Collections.Immutable;
using Pongsole.Contracts;

namespace Pongsole.Client;

internal sealed class BallPrediction(TimeProvider clock)
{
    private const double MinimumY = 0.5;
    private const double MaximumY = 29.5;
    private const double MinimumX = -0.5;
    private const double MaximumX = 100.5;
    private const double LeftFace = 3;
    private const double RightFace = 97;
    private const double CorrectionMilliseconds = 100;
    private const double MaximumCorrection = 8;

    private Dictionary<long, Track> tracks = [];
    private double leadMilliseconds;
    private long timestamp;

    public void Accept(ArenaState arena, int? latencyMilliseconds, long now, bool reset)
    {
        var next = new Dictionary<long, Track>(arena.Balls.Length);
        var lead = Math.Clamp((latencyMilliseconds ?? 0) / 2.0, 0, 125);
        if (arena.Phase == Phase.Playing)
        {
            foreach (var ball in arena.Balls)
            {
                var track = new Track(ball, 0, 0);
                if (!reset && tracks.TryGetValue(ball.Id, out var previous)
                    && Math.Sign(previous.Ball.VelocityX) == Math.Sign(ball.VelocityX))
                {
                    var displayed = Render(previous, clock.GetElapsedTime(timestamp, now).TotalMilliseconds, leadMilliseconds);
                    var projected = Render(track, 0, lead);
                    var x = displayed.X - projected.X;
                    var y = displayed.Y - projected.Y;
                    if (x * x + y * y <= MaximumCorrection * MaximumCorrection)
                    {
                        track = new Track(ball, x, y);
                    }
                }
                next.Add(ball.Id, track);
            }
        }

        tracks = next;
        leadMilliseconds = lead;
        timestamp = now;
    }

    public ImmutableArray<BallState> Read(ArenaState arena, long now)
    {
        if (arena.Phase != Phase.Playing)
        {
            return arena.Balls;
        }

        var age = clock.GetElapsedTime(timestamp, now).TotalMilliseconds;
        return [.. arena.Balls.Select(ball => Render(tracks[ball.Id], age, leadMilliseconds))];
    }

    private static BallState Render(Track track, double ageMilliseconds, double lead)
    {
        var ball = track.Ball;
        var seconds = Math.Clamp(lead + ageMilliseconds, 0, 250) / 1000;
        var minimum = MinimumX;
        var maximum = MaximumX;
        if (ball.VelocityX < 0)
        {
            minimum = ball.X >= LeftFace ? LeftFace : MinimumX;
            seconds = Math.Min(seconds, Math.Max(0, (minimum - ball.X) / ball.VelocityX));
        }
        else if (ball.VelocityX > 0)
        {
            maximum = ball.X <= RightFace ? RightFace : MaximumX;
            seconds = Math.Min(seconds, Math.Max(0, (maximum - ball.X) / ball.VelocityX));
        }

        var correction = 1 - Math.Clamp(ageMilliseconds / CorrectionMilliseconds, 0, 1);
        return ball with
        {
            X = Math.Clamp(ball.X + ball.VelocityX * seconds + track.X * correction, minimum, maximum),
            Y = Math.Clamp(Reflect(ball.Y + ball.VelocityY * seconds) + track.Y * correction, MinimumY, MaximumY)
        };
    }

    private static double Reflect(double y)
    {
        const double span = MaximumY - MinimumY;
        var offset = ((y - MinimumY) % (2 * span) + 2 * span) % (2 * span);
        return MinimumY + Math.Min(offset, 2 * span - offset);
    }

    private readonly record struct Track(BallState Ball, double X, double Y);
}
