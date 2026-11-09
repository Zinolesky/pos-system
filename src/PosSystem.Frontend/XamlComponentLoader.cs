using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace PosSystem.Frontend;

public static class XamlComponentLoader
{
    private static readonly Regex XClassRegex = new(@"x:Class\s*=\s*""[^""]+""", RegexOptions.Compiled);

    public static void LoadWindow(Window target, string bamlRelativeUri, string embeddedResourceName)
    {
        string? xaml = ReadXamlResource(embeddedResourceName);
        if (!string.IsNullOrEmpty(xaml))
        {
            xaml = XClassRegex.Replace(xaml, string.Empty);
            if (XamlReader.Parse(xaml) is Window parsedWindow)
            {
                target.Title = parsedWindow.Title;
                target.WindowState = parsedWindow.WindowState;
                target.Background = parsedWindow.Background;
                target.Foreground = parsedWindow.Foreground;
                target.FontFamily = parsedWindow.FontFamily;
                foreach (var key in parsedWindow.Resources.Keys)
                {
                    target.Resources[key] = parsedWindow.Resources[key];
                }
                var content = parsedWindow.Content;
                parsedWindow.Content = null;
                target.Content = content;
                return;
            }
        }

        Application.LoadComponent(target, new Uri(bamlRelativeUri, UriKind.Relative));
    }

    public static void LoadUserControl(UserControl target, string bamlRelativeUri, string embeddedResourceName)
    {
        string? xaml = ReadXamlResource(embeddedResourceName);
        if (!string.IsNullOrEmpty(xaml))
        {
            xaml = XClassRegex.Replace(xaml, string.Empty);
            if (XamlReader.Parse(xaml) is UserControl parsedControl)
            {
                target.Background = parsedControl.Background;
                target.Foreground = parsedControl.Foreground;
                target.FontFamily = parsedControl.FontFamily;
                foreach (var key in parsedControl.Resources.Keys)
                {
                    target.Resources[key] = parsedControl.Resources[key];
                }
                var content = parsedControl.Content;
                parsedControl.Content = null;
                target.Content = content;
                return;
            }
        }

        Application.LoadComponent(target, new Uri(bamlRelativeUri, UriKind.Relative));
    }

    private static string? ReadXamlResource(string embeddedResourceName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(embeddedResourceName);
        if (stream != null)
        {
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        return null;
    }
}
