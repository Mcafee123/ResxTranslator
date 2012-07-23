using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ResxTranslator
{
    public class ResourceRow
    {
        private static string FormatString { get; set; }
        public string AssemblyName { get; set; }
        public string ResourcefileName { get; set; }
        public string Key { get; set; }
        public string NoLanguageText { get; set; }
        private List<ResourceCell> Translations { get; set; }

        static ResourceRow()
        {
            FormatString = "{0};{1}";
        }

        public ResourceRow(string assemblyName, string resourcefileName, string key, string noLanguageText)
        {
            AssemblyName = assemblyName;
            ResourcefileName = resourcefileName;
            Key = key;
            NoLanguageText = noLanguageText;
            Translations = new List<ResourceCell>();
        }

        public void AddTranslation(string ciShort, string translatedText)
        {
            Translations.Add(new ResourceCell(ciShort, translatedText));
        }

        public override string ToString()
        {
            var strg = String.Format(FormatString, Key, GetTextCheckLinebreaks(NoLanguageText));
            strg = Translations.Aggregate(strg, (current, translation) => current + (";" + GetTextCheckLinebreaks(translation.Value)));
            return strg + ";" + AssemblyName + ";" + ResourcefileName;
        }

        private string GetTextCheckLinebreaks(string input)
        {
            if (input.IndexOf(Environment.NewLine, System.StringComparison.Ordinal) > -1)
            {
                input = "<containslinebreaks>" + input.Replace(Environment.NewLine, "<br />") + "</containslinebreaks>";
            }
            return input;
        }

        public bool HasOpenTranslations()
        {
            return Translations.Any(t => t.MustBeTranslated());
        }

        public static string GetHeaderNames()
        {
            return "Key;Deutsch;Französisch;Italienisch;AssemblyName;ResourcefileName";
        }
    }
}
