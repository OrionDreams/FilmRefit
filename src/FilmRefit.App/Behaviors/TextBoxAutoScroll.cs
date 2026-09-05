using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FilmRefit.App.Behaviors;

public class TextBoxAutoScroll
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBoxAutoScroll, TextBox, bool>("IsEnabled");

    private static readonly AttachedProperty<AutoScrollState?> StateProperty =
        AvaloniaProperty.RegisterAttached<TextBoxAutoScroll, TextBox, AutoScrollState?>("State");

    static TextBoxAutoScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(TextBox textBox)
    {
        return textBox.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(TextBox textBox, bool value)
    {
        textBox.SetValue(IsEnabledProperty, value);
    }

    private static AutoScrollState? GetState(TextBox textBox)
    {
        return textBox.GetValue(StateProperty);
    }

    private static void SetState(TextBox textBox, AutoScrollState? value)
    {
        textBox.SetValue(StateProperty, value);
    }

    private static void OnIsEnabledChanged(TextBox textBox, AvaloniaPropertyChangedEventArgs args)
    {
        GetState(textBox)?.Dispose();
        SetState(textBox, args.GetNewValue<bool>() ? new AutoScrollState(textBox) : null);
    }

    private sealed class AutoScrollState : IDisposable
    {
        private const double BottomTolerance = 1;

        private readonly TextBox _textBox;
        private ScrollViewer? _scrollViewer;
        private bool _shouldAutoScroll = true;
        private bool _isDisposed;

        public AutoScrollState(TextBox textBox)
        {
            _textBox = textBox;
            _textBox.TextChanged += OnTextChanged;
            _textBox.AttachedToVisualTree += OnAttachedToVisualTree;
            _textBox.DetachedFromVisualTree += OnDetachedFromVisualTree;
            Dispatcher.UIThread.Post(AttachScrollViewer);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _textBox.TextChanged -= OnTextChanged;
            _textBox.AttachedToVisualTree -= OnAttachedToVisualTree;
            _textBox.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            DetachScrollViewer();
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
        {
            Dispatcher.UIThread.Post(AttachScrollViewer);
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
        {
            DetachScrollViewer();
        }

        private void OnTextChanged(object? sender, TextChangedEventArgs args)
        {
            ScrollToEndIfNeeded();
        }

        private void AttachScrollViewer()
        {
            if (_isDisposed || _scrollViewer is not null)
            {
                return;
            }

            _textBox.ApplyTemplate();
            _scrollViewer = _textBox.FindDescendantOfType<ScrollViewer>();
            if (_scrollViewer is null)
            {
                return;
            }

            _scrollViewer.ScrollChanged += OnScrollChanged;
            ScrollToEndIfNeeded();
        }

        private void DetachScrollViewer()
        {
            if (_scrollViewer is null)
            {
                return;
            }

            _scrollViewer.ScrollChanged -= OnScrollChanged;
            _scrollViewer = null;
        }

        private void OnScrollChanged(object? sender, ScrollChangedEventArgs args)
        {
            if (Math.Abs(args.OffsetDelta.Y) <= double.Epsilon)
            {
                return;
            }

            _shouldAutoScroll = IsScrolledToBottom();
        }

        private void ScrollToEndIfNeeded()
        {
            if (!_shouldAutoScroll || _isDisposed)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (_isDisposed || !_shouldAutoScroll)
                {
                    return;
                }

                AttachScrollViewer();
                if (_scrollViewer is not null)
                {
                    _scrollViewer.Offset = _scrollViewer.Offset.WithY(GetMaximumVerticalOffset());
                }
            });
        }

        private bool IsScrolledToBottom()
        {
            if (_scrollViewer is null)
            {
                return true;
            }

            return _scrollViewer.Offset.Y >= GetMaximumVerticalOffset() - BottomTolerance;
        }

        private double GetMaximumVerticalOffset()
        {
            if (_scrollViewer is null)
            {
                return 0;
            }

            return Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
        }
    }
}
