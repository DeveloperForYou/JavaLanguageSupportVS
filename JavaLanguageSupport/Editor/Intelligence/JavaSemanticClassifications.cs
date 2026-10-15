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
        internal const string Variable = "Java JDT LS - Variables";
        internal const string Parameter = "Java JDT LS - Parameters";
        internal const string Annotation = "Java JDT LS - Annotations";
        internal const string DirectiveKeyword = "Java JDT LS - Package Import Keywords";
        internal const string Namespace = "Java JDT LS - Package Namespaces";
        internal const string Bracket = "Java JDT LS - Brackets";
        internal const string Separator = "Java JDT LS - Separators";

#pragma warning disable 0649
        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Type)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition TypeDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Method)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition MethodDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Variable)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition VariableDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Parameter)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition ParameterDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Annotation)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition AnnotationDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(DirectiveKeyword)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition DirectiveKeywordDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Namespace)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition NamespaceDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Bracket)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition BracketDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(Separator)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition SeparatorDefinition;
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
                ? Color.FromRgb(0xFF, 0xD1, 0x66)  // dark VS: distinct gold
                : Color.FromRgb(0x79, 0x4F, 0x12); // light VS: dark brown
        }
    }
    // Extra classifications deliberately DO NOT reuse the generic VS identifier
    // classification: that classification is white in many themes.
    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Variable)]
    [Name("Java JDT LS - Variable Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaSemanticVariableFormat : ClassificationFormatDefinition
    {
        public JavaSemanticVariableFormat()
        {
            DisplayName = "Java: variabili e campi (JDT LS)";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x9C, 0xDC, 0xFE)
                : Color.FromRgb(0x00, 0x10, 0x80);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Parameter)]
    [Name("Java JDT LS - Parameter Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaSemanticParameterFormat : ClassificationFormatDefinition
    {
        public JavaSemanticParameterFormat()
        {
            DisplayName = "Java: parametri (JDT LS)";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0xD7, 0xBA, 0x7D)
                : Color.FromRgb(0x80, 0x53, 0x00);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Annotation)]
    [Name("Java JDT LS - Annotation Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaSemanticAnnotationFormat : ClassificationFormatDefinition
    {
        public JavaSemanticAnnotationFormat()
        {
            DisplayName = "Java: annotazioni (JDT LS)";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0xC5, 0x86, 0xC0)
                : Color.FromRgb(0xAF, 0x00, 0xDB);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.DirectiveKeyword)]
    [Name("Java JDT LS - Package Import Keyword Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaDirectiveKeywordFormat : ClassificationFormatDefinition
    {
        public JavaDirectiveKeywordFormat()
        {
            DisplayName = "Java: parole chiave package/import";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0xC5, 0x86, 0xC0) // violet
                : Color.FromRgb(0x84, 0x1F, 0xA2);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Namespace)]
    [Name("Java JDT LS - Package Namespace Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaNamespaceFormat : ClassificationFormatDefinition
    {
        public JavaNamespaceFormat()
        {
            DisplayName = "Java: package e namespace degli import";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x82, 0xB8, 0xE6) // distinct from method yellow and class teal
                : Color.FromRgb(0x19, 0x55, 0x95);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Bracket)]
    [Name("Java JDT LS - Bracket Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaBracketFormat : ClassificationFormatDefinition
    {
        public JavaBracketFormat()
        {
            DisplayName = "Java: parentesi tonde, quadre e graffe";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0xDA, 0xAE, 0xF0) // lilac - not method yellow
                : Color.FromRgb(0x7E, 0x34, 0x98);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaSemanticClassificationNames.Separator)]
    [Name("Java JDT LS - Separator Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaSeparatorFormat : ClassificationFormatDefinition
    {
        public JavaSeparatorFormat()
        {
            DisplayName = "Java: separatori punto, virgola e punto e virgola";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0xBE, 0xCD, 0xDF) // light cool gray
                : Color.FromRgb(0x51, 0x5D, 0x6D);
        }
    }

}
