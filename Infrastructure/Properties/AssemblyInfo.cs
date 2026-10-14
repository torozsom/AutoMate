using System.Runtime.CompilerServices;

// Allows the regression test assembly to exercise internal adapters without widening their public API.
[assembly: InternalsVisibleTo("Test")]
