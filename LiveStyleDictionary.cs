using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;

namespace Cdiary;

// Each dispatcher owns its resources; hidden STA previews and the app never share WPF objects across threads.
public sealed class LiveStyleDictionary : ResourceDictionary
{
    private static readonly ConditionalWeakTable<Dispatcher, StyleScope> Scopes = new();
    internal static StyleScope ForDispatcher(Dispatcher dispatcher) => Scopes.GetValue(dispatcher, _ => new StyleScope());
    public LiveStyleDictionary() => ForDispatcher(Dispatcher.CurrentDispatcher).Attach(this);

    internal sealed class StyleScope
    {
        private readonly List<WeakReference<LiveStyleDictionary>> _hosts = [];
        public ResourceDictionary Current { get; private set; } = (ResourceDictionary)System.Windows.Application.LoadComponent(
            new Uri("/Cdiary;component/Styles/SharedStyles.xaml", UriKind.Relative));

        public void Attach(LiveStyleDictionary host)
        {
            _hosts.RemoveAll(reference => !reference.TryGetTarget(out _));
            _hosts.Add(new(host)); host.MergedDictionaries.Add(Current);
        }
        public void Replace(ResourceDictionary candidate)
        {
            var previous = Current;
            var hosts = _hosts.Select(reference => reference.TryGetTarget(out var host) ? host : null)
                .Where(host => host is not null).Cast<LiveStyleDictionary>().ToArray();
            try
            {
                foreach (var host in hosts) host.MergedDictionaries[0] = candidate;
                Current = candidate;
            }
            catch
            {
                foreach (var host in hosts) host.MergedDictionaries[0] = previous;
                throw;
            }
            _hosts.RemoveAll(reference => !reference.TryGetTarget(out _));
        }
    }
}
