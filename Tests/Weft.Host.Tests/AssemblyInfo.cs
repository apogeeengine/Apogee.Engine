using Xunit;

// A session owns the process's one RmlUi state (and other per-process state), so two sessions cannot exist at once: the tests that start one run one after the other.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
