using Xunit;

// Every test in this assembly drives external processes: restic, the password helper, the recovery
// tool, and the platform's TPM. Running them at once turns their timings into a lottery - a backup
// that normally starts in a second can take minutes when a dozen of them compete for the same disk -
// and a test that then fails says nothing about the code. They run one at a time instead.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
