using System;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.ProjectSystem;

namespace JavaLanguageSupport.ProjectSystem.Icons
{
    /// <summary>
    /// Solo per i progetti .javaproj (JavaLanguageSupportCps).
    /// Personalizza esclusivamente le icone: non modifica nodi, nomi o capability.
    /// </summary>
    [Export(typeof(IProjectTreePropertiesProvider))]
    [AppliesTo("JavaLanguageSupportCps")]
    [Order(1000)]
    internal sealed class JavaProjectTreeIconProvider : IProjectTreePropertiesProvider
    {
        private static readonly Guid ProjectIconGuid =
            new Guid("d203fa12-c9a4-47a1-8f62-39afba8d2fa0");

        public void CalculatePropertyValues(
            IProjectTreeCustomizablePropertyContext propertyContext,
            IProjectTreeCustomizablePropertyValues propertyValues)
        {
            if (propertyValues.Flags.Contains(ProjectTreeFlags.Common.ProjectRoot))
            {
                
                var icon = new ImageMoniker { Guid = ProjectIconGuid, Id = 0 }.ToProjectSystemType();
                propertyValues.Icon = icon;
                propertyValues.ExpandedIcon = icon;
                return;
            }

            // Non sovrascrive cartelle, file esclusi o altri formati (pom.xml ecc.).
            if (!propertyValues.Flags.Contains(ProjectTreeFlags.Common.FileOnDisk) ||
                propertyValues.Flags.Contains(ProjectTreeFlags.Common.Folder) ||
                string.IsNullOrEmpty(propertyContext.ItemName) ||
                !propertyContext.ItemName.EndsWith(".java", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Moniker Java ufficiale VS: adattamento automatico a Light / Dark / DPI.
            var javaIcon = KnownMonikers.JavaSource.ToProjectSystemType();
            propertyValues.Icon = javaIcon;
            propertyValues.ExpandedIcon = javaIcon;
        }
    }
}
