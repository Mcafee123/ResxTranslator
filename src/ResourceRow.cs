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
        private List<ResourceCell> TranslationList { get; set; }

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
            TranslationList = new List<ResourceCell>();
        }

        public void AddTranslation(string ciShort, string translatedText)
        {
            TranslationList.Add(new ResourceCell(ciShort, translatedText));
        }

        public IEnumerable<ResourceCell> Translations
        {
            get { return TranslationList; }
        } 

        public override string ToString()
        {
            var strg = String.Format(FormatString, Key, GetTextCheckLinebreaks(NoLanguageText));
            strg = TranslationList.Aggregate(strg, (current, translation) => current + (";" + GetTextCheckLinebreaks(translation.Value)));
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
            return TranslationList.Any(t => t.MustBeTranslated());
        }

        public static string GetHeaderNames()
        {
            return "Key;Deutsch;Französisch;Italienisch;AssemblyName;ResourcefileName";
        }

        public static ResourceRow Parse(string csvLine, char separator)
        {
            var cells = csvLine.Split(separator);
            var key = cells[0];
            var noLang = cells[1];
            var assemblyName = cells[cells.Length - 2];
            var resourceFileName = cells[cells.Length - 1];
            var row = new ResourceRow(assemblyName, resourceFileName, key,  noLang);

            row.AddTranslation("fr", cells[2]);
            row.AddTranslation("it", cells[3]);

            return row;
        }

        public static ResourceRow Parse(string csvLine)
        {
            return Parse(csvLine, ';');
        }
    }
}
