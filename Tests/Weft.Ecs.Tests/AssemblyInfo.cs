using Xunit;

// Several tests listen to the process-wide log (Log.AddSink) and assert on the warnings they see; a test of another class that logs at the same moment would end up in their sink.
// The project is small and fast, so all its tests are one collection and run one after the other.
[assembly: CollectionBehavior(CollectionBehavior.CollectionPerAssembly)]
