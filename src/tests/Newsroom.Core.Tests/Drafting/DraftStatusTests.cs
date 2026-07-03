using Newsroom.Core.Drafting;

namespace Newsroom.Core.Tests.Drafting;

public class DraftStatusTests
{
    [Fact]
    public void Superseded_exists_for_the_change_request_transition()
    {
        // ✏️ Промени: the replaced version becomes Superseded and a new Generating row takes
        // over (docs/02-functional-spec.md state machine).
        Assert.True(Enum.IsDefined(DraftStatus.Superseded));
    }
}
