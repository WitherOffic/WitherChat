using WitherChat.Desktop.Platforms.Linux;
using WitherChat.Desktop.Platforms.macOS;
using WitherChat.Desktop.Platforms.Windows;

namespace WitherChat.Desktop.Platforms;

public static class PlatformDescriptorFactory
{
    public static IPlatformDescriptor CreateCurrent()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformDescriptor();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsPlatformDescriptor();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxPlatformDescriptor();
        }

        throw new PlatformNotSupportedException("WitherChat supports Windows, Linux and macOS.");
    }
}
