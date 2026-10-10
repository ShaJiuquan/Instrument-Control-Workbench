namespace InstrumentControlWorkbench.Core.Routing;

// Native authored configuration format; it carries physical layouts, not driver instances.
public interface IRoutingProfileDocument
{
    string Serialize(RoutingProfileCatalog catalog);
    RoutingProfileCatalog Parse(string text);
    Task<RoutingProfileCatalog> LoadAsync(string path, CancellationToken token);
    Task SaveAsync(string path, RoutingProfileCatalog catalog, CancellationToken token);
}
