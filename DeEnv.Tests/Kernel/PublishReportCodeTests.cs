using DeEnv.Code;
using DeEnv.Kernel;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace DeEnv.Tests.Kernel;

public sealed class PublishReportCodeTests
{
    [Test]
    public async Task Migration_only_report_is_not_empty()
    {
        var report = new PublishReport
        {
            Applied = false,
            DryRun = true,
            BaseCommit = 1,
            TargetCommit = 2,
            UncommittedDrift = false,
            Renames = [],
            Adds = [],
            Removes = [],
            Conversions = [],
            Cardinality = [],
            FallbackNameMatched = false,
            Migrations = [new MigrationRunReport(2, "compute", ["Item"], 1)],
        };

        var value = (ExecObject)PublishReportCode.Build(report, targetVersion: 10, new ExecContext());

        await Assert.That(((ExecBool)value.Props["isEmpty"]).Value).IsFalse();
    }
}
