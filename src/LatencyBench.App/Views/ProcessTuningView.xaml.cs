using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LatencyBench.App.ViewModels;

namespace LatencyBench.App.Views;

public partial class ProcessTuningView : UserControl
{
    public ProcessTuningView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ProcessTuningViewModel oldViewModel)
        {
            oldViewModel.Processes.CollectionChanged -= OnProcessesChanged;
            foreach (ProcessRowViewModel row in oldViewModel.Processes)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        }

        if (e.NewValue is ProcessTuningViewModel newViewModel)
        {
            newViewModel.Processes.CollectionChanged += OnProcessesChanged;
            foreach (ProcessRowViewModel row in newViewModel.Processes)
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }
    }

    private void OnProcessesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Refresh() clears the collection wholesale (a Reset, with no OldItems) rather than removing
        // rows one at a time. There is nothing to unsubscribe from in that case: the discarded
        // ProcessRowViewModel instances are no longer referenced by anything else, including this
        // view, so they are collectible regardless of the handler still attached to their own
        // PropertyChanged field.
        if (e.OldItems is not null)
        {
            foreach (ProcessRowViewModel row in e.OldItems)
            {
                row.PropertyChanged -= OnRowPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (ProcessRowViewModel row in e.NewItems)
            {
                row.PropertyChanged += OnRowPropertyChanged;
            }
        }
    }

    /// <summary>
    /// Clears keyboard focus specifically when a row's action just failed. A failed click leaves
    /// WPF's default keyboard-focus adorner on the button that was pressed, which reads as a
    /// lingering "selected" state even though nothing about the row's bound state actually changed —
    /// see <see cref="ProcessRowViewModel.RowError"/>. This intentionally only reacts to
    /// <c>RowError</c>; it does not touch focus visuals in general, so normal keyboard navigation and
    /// accessibility are unaffected.
    /// </summary>
    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProcessRowViewModel.RowError))
        {
            return;
        }

        if (sender is ProcessRowViewModel { RowError: not null })
        {
            Keyboard.ClearFocus();
        }
    }
}
