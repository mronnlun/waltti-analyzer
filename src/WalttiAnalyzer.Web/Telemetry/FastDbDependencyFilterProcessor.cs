using System.Diagnostics;
using OpenTelemetry;

namespace WalttiAnalyzer.Web.Telemetry;

/// <summary>
/// Suppresses fast, successful database dependency telemetry so that
/// Application Insights / Log Analytics only ingests slow or failed DB calls.
/// </summary>
public class FastDbDependencyFilterProcessor : BaseProcessor<Activity>
{
    private static readonly TimeSpan DurationThreshold = TimeSpan.FromMilliseconds(500);

    public override void OnEnd(Activity activity)
    {
        if (activity.Kind == ActivityKind.Client
            && activity.GetTagItem("db.system") is string
            && activity.Status != ActivityStatusCode.Error
            && activity.Duration < DurationThreshold)
        {
            // Clear the Recorded flag so the export processor skips this span.
            activity.ActivityTraceFlags &= ~ActivityTraceFlags.Recorded;
        }

        base.OnEnd(activity);
    }
}
