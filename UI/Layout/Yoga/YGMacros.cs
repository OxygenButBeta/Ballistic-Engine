namespace Facebook.Yoga
{
    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public sealed class YogaDeprecatedAttribute : Attribute
    {
        public string Message { get; }

        public YogaDeprecatedAttribute(string message)
        {
            Message = message;
        }
    }
}

