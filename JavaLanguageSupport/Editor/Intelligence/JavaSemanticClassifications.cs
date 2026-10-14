using System;
using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Intelligence
{
    /// <summary>
    /// Separate Java-specific editor classifications; the built-in Type and Method
    /// classes can have identical colors in a user's Visual Studio theme.
    /// All colors are user-adjustable under Tools > Options > Fonts and Colors.
    /// </summary>
    internal static class JavaSemanticClassificationNames
    {
        internal const string Type = "Java JDT LS - Types";
        internal const string Method = "Java JDT LS - Methods";

#pragma warning disable 0649
        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Type)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition TypeDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Method)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition MethodDefinition;
#pragma warning restore 0649
    }

    internal static class JavaSemanticPalette
    {
        internal static bool DarkTheme
        {
            get
            {
                try
                {
                    var background = VSColorTheme.GetThemedColor(
                        EnvironmentColors.ToolWindowBackgroundColorKey);
                    return (background.R * 299 + background.G * 587 + background.B * 114) < 128000;
                }
                catch (Exception) { return true; }
            }
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Type)]
    [Name("Java JDT LS - Type Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaSemanticTypeFormat : ClassificationFormatDefinition
    {
        public JavaSemanticTypeFormat()
        {
            DisplayName = "Java: tipi, classi e interfacce (JDT LS)";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x4E, 0xC9, 0xB0)  // dark VS: teal
                : Color.FromRgb(0x26, 0x7F, 0x99); // light VS: dark teal
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Method)]
    [Name("Java JDT LS - Method Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaSemanticMethodFormat : ClassificationFormatDefinition
    {
        public JavaSemanticMethodFormat()
        {
            DisplayName = "Java: metodi e chiamate (JDT LS)";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0xDC, 0xDC, 0xAA)  // dark VS: soft yellow
                : Color.FromRgb(0x79, 0x5E, 0x26); // light VS: brown
        }
    }
}
