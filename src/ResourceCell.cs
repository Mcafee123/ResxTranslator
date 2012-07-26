using System;
using System.Globalization;

namespace ResxTranslator
{
    public class ResourceCell
    {
        public string Value { get; set; }
        public string Culture { get; private set; }
    
        public ResourceCell(string ciShort, string value)
        {
            Culture = ciShort;
            Value = value;
        }

        public bool MustBeTranslated()
        {
            return Value.IndexOf(String.Format("({0})", Culture.ToUpper()), System.StringComparison.Ordinal) > -1;
        }

        public ResourceCell RemoveLanguageSuffixIfExists()
        {
            Value = Value.Replace(String.Format("({0})", Culture.ToUpper()), "").Trim();
            return this;
        }
    }
}
