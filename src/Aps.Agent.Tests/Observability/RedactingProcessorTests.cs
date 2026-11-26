using System.Diagnostics;
using Aps.Agent.Observability;
using Aps.Agent.Security;

namespace Aps.Agent.Tests.Observability;

public sealed class RedactingProcessorTests
{
    [Fact]
    public void Redacts_string_tags_and_status_but_keeps_other_tags()
    {
        using var activity = new Activity("test").Start();
        activity.SetTag("http.request.header", "Cookie: session=abc123secretvalue");
        activity.SetTag("aps.day", 2);
        activity.SetTag("aps.outcome", "candidate");
        activity.SetStatus(ActivityStatusCode.Error, "auth failed: authorization: Bearer eyJhbGciOi.token");

        new RedactingProcessor().OnEnd(activity);

        Assert.Equal($"Cookie: session={Redactor.Redacted}", activity.GetTagItem("http.request.header"));
        Assert.Equal(2, activity.GetTagItem("aps.day"));
        Assert.Equal("candidate", activity.GetTagItem("aps.outcome"));
        Assert.DoesNotContain("eyJhbGciOi", activity.StatusDescription);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
    }
}
