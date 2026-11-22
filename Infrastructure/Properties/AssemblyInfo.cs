using System.Runtime.CompilerServices;

// Allows the regression test assembly to exercise internal adapters without widening their public API.
[assembly: InternalsVisibleTo("Test")]

// Allows isolated module tests to exercise helpers without widening their public API.
[assembly: InternalsVisibleTo("Tests")]
