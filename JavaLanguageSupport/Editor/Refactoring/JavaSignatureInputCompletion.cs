using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JavaLanguageSupport.LanguageServer;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json.Linq;

namespace JavaLanguageSupport.Editor.Refactoring
{
    /// <summary>
    /// Suggests Java types returned by JDT LS workspace/symbol without changing any
    /// editor buffer. The dialog text stays editable, even when JDT LS is not ready.
    /// For default expressions, suggestions are literal templates rather than
    /// fictitious LSP completions in a source file that does not exist.
    /// </summary>
    internal static class JavaSignatureInputCompletion
    {
        private static readonly string[] BuiltinTypes = {
            "void", "boolean", "byte", "short", "int", "long", "float", "double",
            "char", "String", "Object", "Integer", "Long", "Double", "Boolean",
            "List", "ArrayList", "Set", "HashSet", "Map", "HashMap", "Optional",
            "BigDecimal", "LocalDate", "LocalDateTime", "Instant"
        };

        internal static void Attach(TextBox editor, bool isDefaultExpression,
            Func<string> parameterType = null)
        {
            if (editor == null || editor.Tag is CompletionMarker) return;
            var marker = new CompletionMarker(editor, isDefaultExpression, parameterType);
            editor.Tag = marker;
        }

        private sealed class CompletionMarker
        {
            private readonly TextBox editor;
            private readonly bool isDefault;
            private readonly Func<string> parameterType;
            private readonly Popup popup;
            private readonly ListBox list;
            private CancellationTokenSource pending;
            private int requestGeneration;
            private bool applying;

            internal CompletionMarker(TextBox textBox, bool defaultExpression,
                Func<string> parameterType)
            {
                editor = textBox;
                isDefault = defaultExpression;
                this.parameterType = parameterType;
                list = new ListBox { MaxHeight = 210, MinWidth = 230,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                list.SetResourceReference(Control.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                list.SetResourceReference(Control.ForegroundProperty,
                    EnvironmentColors.ToolWindowTextBrushKey);
                list.MouseDoubleClick += (s, e) => Accept();
                popup = new Popup { PlacementTarget = editor,
                    Placement = PlacementMode.Bottom, StaysOpen = false,
                    AllowsTransparency = true, Child = new Border {
                        Child = list, BorderThickness = new Thickness(1) } };
                var border = (Border)popup.Child;
                border.SetResourceReference(Border.BackgroundProperty,
                    EnvironmentColors.ToolWindowBackgroundBrushKey);
                border.SetResourceReference(Border.BorderBrushProperty,
                    EnvironmentColors.ToolWindowBorderBrushKey);
                editor.TextChanged += OnTextChanged;
                editor.PreviewKeyDown += OnKeyDown;
                editor.Unloaded += (s, e) => { StopRequest(); popup.IsOpen = false; };
            }

            private void StopRequest()
            {
                if (pending == null) return;
                pending.Cancel(); pending.Dispose(); pending = null;
            }

            private void OnTextChanged(object sender, TextChangedEventArgs args)
            {
                if (applying || !editor.IsKeyboardFocusWithin) return;
                RefreshSuggestions(false);
            }

            // CTRL+SPACE is an explicit completion request, including on an
            // empty cell. This popup is not the editor's CompletionBroker:
            // workspace/symbol provides Java types, not expression completions.
            private void RefreshSuggestions(bool explicitRequest)
            {
                StopRequest();
                int generation = ++requestGeneration;
                string input = PrefixAtCaret();
                if (input.Length == 0 && !explicitRequest)
                { popup.IsOpen = false; return; }
                var local = LocalSuggestions(input);
                Show(local);
                if (isDefault || input.Length < (explicitRequest ? 1 : 2) ||
                    !JavaLspRpc.Connected) return;
                var source = new CancellationTokenSource();
                pending = source;
                CancellationToken requestToken = source.Token;
                ThreadHelper.JoinableTaskFactory.RunAsync(async delegate
                {
                    try
                    {
                        await Task.Delay(430, requestToken).ConfigureAwait(false);
                        // JDT LS returns workspace symbols, including project types.
                        // This is a suggestion only: types are validated by the
                        // actual change-signature refactoring when the user applies it.
                        JToken result = await JavaLspRpc.RequestAsync("workspace/symbol",
                            new { query = input }, requestToken).ConfigureAwait(false);
                        requestToken.ThrowIfCancellationRequested();
                        var symbols = new List<string>();
                        JArray array = result as JArray;
                        if (array != null)
                        {
                            foreach (JObject item in array.OfType<JObject>().Take(80))
                            {
                                int kind = (int?)item["kind"] ?? 0;
                                if (kind != 5 && kind != 10 && kind != 11 && kind != 23) continue;
                                string name = (string)item["name"];
                                string owner = (string)item["containerName"];
                                if (string.IsNullOrWhiteSpace(name)) continue;
                                symbols.Add(name);
                                if (!string.IsNullOrWhiteSpace(owner))
                                    symbols.Add(owner + "." + name);
                            }
                        }
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(requestToken);
                        if (generation != requestGeneration || !editor.IsKeyboardFocusWithin)
                            return;
                        Show(local.Concat(symbols)
                            .Where(x => x.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(30).ToList());
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.WriteLine("[Java Signature Suggestions] " + ex.Message);
                    }
                }).FileAndForget("JavaLanguageSupport/SignatureTypeSuggestions");
            }

            private string PrefixAtCaret()
            {
                if (isDefault) return editor.Text.Substring(0,
                    Math.Max(0, Math.Min(editor.CaretIndex, editor.Text.Length))).TrimStart();
                int position = Math.Max(0, Math.Min(editor.CaretIndex, editor.Text.Length));
                int start = position;
                while (start > 0 && (char.IsLetterOrDigit(editor.Text[start - 1]) ||
                       editor.Text[start - 1] == '_' || editor.Text[start - 1] == '$'))
                    start--;
                return editor.Text.Substring(start, position - start);
            }

            private List<string> LocalSuggestions(string input)
            {
                IEnumerable<string> choices;
                if (!isDefault) choices = BuiltinTypes;
                else
                {
                    string type = parameterType == null ? "" : parameterType() ?? "";
                    type = type.Trim();
                    if (type == "boolean" || type == "Boolean")
                        choices = new[] { "false", "true", "null" };
                    else if (type == "char" || type == "Character")
                        choices = new[] { "'\\0'", "'a'" };
                    else if (type == "String")
                        choices = new[] { "\"\"", "null" };
                    else if (new[] { "int", "long", "short", "byte", "float", "double" }.Contains(type))
                        choices = new[] { "0", "0L", "0.0", "0.0f" };
                    else choices = new[] { "null", "new ", "java.util.Collections.emptyList()" };
                }
                return choices.Where(x => x.StartsWith(input, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToList();
            }

            private void Show(IEnumerable<string> suggestions)
            {
                var entries = suggestions.ToList();
                list.ItemsSource = entries;
                popup.IsOpen = entries.Count > 0;
                if (entries.Count > 0) list.SelectedIndex = 0;
            }

            private void Accept()
            {
                string value = list.SelectedItem as string;
                if (string.IsNullOrWhiteSpace(value)) return;
                applying = true;
                try
                {
                    if (isDefault)
                    {
                        editor.Text = value;
                        editor.CaretIndex = value.Length;
                    }
                    else
                    {
                        int end = Math.Max(0, Math.Min(editor.CaretIndex, editor.Text.Length));
                        int start = end;
                        while (start > 0 && (char.IsLetterOrDigit(editor.Text[start - 1]) ||
                            editor.Text[start - 1] == '_' || editor.Text[start - 1] == '$'))
                            start--;
                        editor.Text = editor.Text.Substring(0, start) + value +
                            editor.Text.Substring(end);
                        editor.CaretIndex = start + value.Length;
                    }
                }
                finally { applying = false; popup.IsOpen = false; }
                editor.Focus();
            }

            private void OnKeyDown(object sender, KeyEventArgs args)
            {
                if (args.Key == Key.Space &&
                    (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                {
                    args.Handled = true;
                    RefreshSuggestions(true);
                    return;
                }
                if (!popup.IsOpen) return;
                if (args.Key == Key.Escape)
                {
                    popup.IsOpen = false; args.Handled = true;
                }
                else if (args.Key == Key.Down)
                {
                    list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                    args.Handled = true;
                }
                else if (args.Key == Key.Up)
                {
                    list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                    args.Handled = true;
                }
                else if (args.Key == Key.Tab || args.Key == Key.Return)
                {
                    Accept(); args.Handled = true;
                }
            }
        }
    }
}
