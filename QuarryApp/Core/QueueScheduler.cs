using System.Windows.Threading;

namespace QuarryApp.Core;

/// <summary>
/// Queue Scheduler for off-peak downloads and time-window execution.
/// Supports automated queue start/stop times and speed-limit profiles.
/// </summary>
public class QueueScheduler
{
    private readonly DispatcherTimer _timer;
    private readonly Action _onStartQueue;
    private readonly Action _onStopQueue;

    public bool IsSchedulerEnabled { get; set; }
    public TimeSpan? ScheduledStartTime { get; set; }
    public TimeSpan? ScheduledStopTime { get; set; }

    public QueueScheduler(Action onStartQueue, Action onStopQueue)
    {
        _onStartQueue = onStartQueue;
        _onStopQueue = onStopQueue;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += Timer_Tick;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    private void Timer_Tick(object? sender, EventArgs e)
    {
        if (!IsSchedulerEnabled) return;

        var now = DateTime.Now.TimeOfDay;

        if (ScheduledStartTime.HasValue)
        {
            var start = ScheduledStartTime.Value;
            if (Math.Abs((now - start).TotalSeconds) < 20)
            {
                _onStartQueue();
            }
        }

        if (ScheduledStopTime.HasValue)
        {
            var stop = ScheduledStopTime.Value;
            if (Math.Abs((now - stop).TotalSeconds) < 20)
            {
                _onStopQueue();
            }
        }
    }
}
