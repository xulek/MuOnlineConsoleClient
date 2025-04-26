using Avalonia.Data.Converters;

namespace MuOnlineConsole.GUI.Converters
{
    public static class ObjectConverters
    {
        /// <summary>
        /// Converter checking if an object is not null. Used for IsEnabled.
        /// </summary>
        public static readonly IValueConverter IsNotNull =
            new FuncValueConverter<object?, bool>(x => x != null);

        /// <summary>
        /// Converter checking if a value is equal to the parameter. Used for IsVisible.
        /// </summary>
        public static readonly IValueConverter IsEqual =
            new FuncValueConverter<object?, object?, bool>((value, parameter) => Equals(value, parameter));

        /// <summary>
        /// Converter checking if a value is NOT equal to the parameter.
        /// </summary>
        public static readonly IValueConverter IsNotEqual =
            new FuncValueConverter<object?, object?, bool>((value, parameter) => !Equals(value, parameter));
    }

    /// <summary>
    /// Converters for number operations for XAML
    /// </summary>
    public static class NumberConverters
    {
        /// <summary>
        /// Adds a value to a numeric property
        /// </summary>
        public static readonly IValueConverter AddTwo = new FuncValueConverter<double, double>(value => value + 2);

        /// <summary>
        /// Subtracts a value from a numeric property
        /// </summary>
        public static readonly IValueConverter SubtractOne = new FuncValueConverter<double, double>(value => value - 1);

        /// <summary>
        /// Multiplies a numeric property by a specified value
        /// </summary>
        public static readonly IValueConverter MultiplyByTwo = new FuncValueConverter<double, double>(value => value * 2);

        /// <summary>
        /// Divides a numeric property by a specified value
        /// </summary>
        public static readonly IValueConverter DivideByTwo = new FuncValueConverter<double, double>(value => value / 2);
    }
}