namespace XIVLauncher.Support;

/// <summary>
/// Generic combined data class.
/// </summary>
/// <typeparam name="TValueType">The type of value.</typeparam>
public class GenericCombinedData<TValueType>
{
    /// <summary>
    /// Gets or sets the name displayed.
    /// </summary>
    public string Display { get; set; }

    /// <summary>
    /// Gets or sets the value.
    /// </summary>
    public TValueType Value { get; set; }
}
