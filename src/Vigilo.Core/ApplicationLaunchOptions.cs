namespace Vigilo.Core;

public sealed record ApplicationLaunchOptions(bool Background)
{
    public static ApplicationLaunchOptions Parse(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return new ApplicationLaunchOptions(arguments.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase)));
    }
}
