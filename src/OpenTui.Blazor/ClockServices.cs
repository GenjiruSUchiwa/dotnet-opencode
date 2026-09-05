namespace OpenTui.Blazor;

// Supply the terminal host's selected BCL clock without mutating or owning the
// caller's service provider. Other service lifetimes remain with that provider.
internal sealed class ClockServices(IServiceProvider services, TimeProvider clock) : IServiceProvider
{
    public object? GetService(Type serviceType) => serviceType == typeof(TimeProvider) ? clock
        : serviceType == typeof(IServiceProvider) ? this : services.GetService(serviceType);
}
