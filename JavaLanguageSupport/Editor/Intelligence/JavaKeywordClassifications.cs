using System.ComponentModel.Composition;
using System.Windows.Media;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace JavaLanguageSupport.Editor.Intelligence
{
    // Visual Studio's Fonts and Colors exposes each category independently.
    // Keep the names stable: they become persistent user setting identifiers.
    internal static class JavaKeywordClassificationNames
    {
        internal const string Keyword = "Java JDT LS - Control Keywords";
        internal const string Primitive = "Java JDT LS - Primitive Types";
        internal const string Var = "Java JDT LS - Var Keyword";
        internal const string Modifier = "Java JDT LS - Modifiers";
        internal const string Constant = "Java JDT LS - Language Constants";

        internal static bool IsNewKeywordClassification(string name)
        {
            return name == Keyword || name == Primitive || name == Var ||
                name == Modifier || name == Constant;
        }
    }

    internal static class JavaKeywordClassificationDefinitions
    {
#pragma warning disable 0649
        [Export(typeof(ClassificationTypeDefinition))]
        [Name(JavaKeywordClassificationNames.Keyword)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition KeywordDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(JavaKeywordClassificationNames.Primitive)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition PrimitiveDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(JavaKeywordClassificationNames.Var)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition VarDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(JavaKeywordClassificationNames.Modifier)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition ModifierDefinition;

        [Export(typeof(ClassificationTypeDefinition))]
        [Name(JavaKeywordClassificationNames.Constant)]
        [BaseDefinition("text")]
        internal static ClassificationTypeDefinition ConstantDefinition;
#pragma warning restore 0649
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaKeywordClassificationNames.Keyword)]
    [Name("Java JDT LS - Control Keywords Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaControlKeywordFormat : ClassificationFormatDefinition
    {
        public JavaControlKeywordFormat()
        {
            DisplayName = "Java: parole chiave";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x56, 0x9C, 0xD6)
                : Color.FromRgb(0x00, 0x00, 0xFF);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaKeywordClassificationNames.Primitive)]
    [Name("Java JDT LS - Primitive Types Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaPrimitiveKeywordFormat : ClassificationFormatDefinition
    {
        public JavaPrimitiveKeywordFormat()
        {
            DisplayName = "Java: tipi primitivi";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x56, 0x9C, 0xD6)
                : Color.FromRgb(0x00, 0x00, 0xFF);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaKeywordClassificationNames.Var)]
    [Name("Java JDT LS - Var Keyword Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaVarKeywordFormat : ClassificationFormatDefinition
    {
        public JavaVarKeywordFormat()
        {
            DisplayName = "Java: parola chiave var";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x56, 0x9C, 0xD6)
                : Color.FromRgb(0x00, 0x00, 0xFF);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaKeywordClassificationNames.Modifier)]
    [Name("Java JDT LS - Modifiers Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaModifierKeywordFormat : ClassificationFormatDefinition
    {
        public JavaModifierKeywordFormat()
        {
            DisplayName = "Java: modificatori";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x56, 0x9C, 0xD6)
                : Color.FromRgb(0x00, 0x00, 0xFF);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = JavaKeywordClassificationNames.Constant)]
    [Name("Java JDT LS - Language Constants Format")]
    [UserVisible(true)]
    [Order(After = Priority.High)]
    internal sealed class JavaConstantKeywordFormat : ClassificationFormatDefinition
    {
        public JavaConstantKeywordFormat()
        {
            DisplayName = "Java: costanti";
            ForegroundColor = JavaSemanticPalette.DarkTheme
                ? Color.FromRgb(0x56, 0x9C, 0xD6)
                : Color.FromRgb(0x00, 0x00, 0xFF);
        }
    }
}
