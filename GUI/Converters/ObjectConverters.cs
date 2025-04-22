// Plik: GUI/Converters/ObjectConverters.cs
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace MuOnlineConsole.GUI.Converters
{
    public static class ObjectConverters
    {
        /// <summary>
        /// Konwerter sprawdzający, czy obiekt nie jest null. Używany do IsEnabled.
        /// </summary>
        public static readonly IValueConverter IsNotNull =
            new FuncValueConverter<object?, bool>(x => x != null);

        /// <summary>
        /// Konwerter sprawdzający, czy wartość jest równa parametrowi. Używany do IsVisible.
        /// </summary>
        public static readonly IValueConverter IsEqual =
            new FuncValueConverter<object?, object?, bool>((value, parameter) => Equals(value, parameter));

        /// <summary>
        /// Konwerter sprawdzający, czy wartość NIE jest równa parametrowi.
        /// </summary>
        public static readonly IValueConverter IsNotEqual =
            new FuncValueConverter<object?, object?, bool>((value, parameter) => !Equals(value, parameter));
    }
}