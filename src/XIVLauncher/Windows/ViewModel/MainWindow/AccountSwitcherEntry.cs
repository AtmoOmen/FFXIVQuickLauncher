using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using XIVLauncher.Account;
using XIVLauncher.Common.Constant;

namespace XIVLauncher.Windows.ViewModel.MainWindow;

internal class AccountSwitcherEntry
{
    public XIVAccount Account { get; set; } = null!;

    public ImageSource ProfileImage { get; set; } = DefaultImage;

    private static readonly ImageSource DefaultImage = CreateDefaultImage();

    public void UpdateProfileImage() =>
        ProfileImage = GetProfileImage(Account);

    public static ImageSource GetProfileImage(XIVAccount account) =>
        TryGetCustomProfileImagePath(account, out var imagePath) ? LoadProfileImage(imagePath) : DefaultImage;

    public static ImageSource GetDefaultProfileImage() =>
        DefaultImage;

    public static ImageSource LoadProfileImageFromPath(string imagePath) =>
        LoadProfileImage(imagePath);

    public static void SaveCustomProfileImage(XIVAccount account, string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(extension))
            throw new InvalidOperationException("所选头像文件缺少可识别的扩展名。");

        var imageBytes            = File.ReadAllBytes(sourcePath);
        var profileImageDirectory = GetProfileImageDirectory();
        Directory.CreateDirectory(profileImageDirectory);
        RemoveCustomProfileImage(account);

        var targetPath = Path.Combine(profileImageDirectory, $"{GetProfileImageFileKey(account)}{extension.ToLowerInvariant()}");
        File.WriteAllBytes(targetPath, imageBytes);
    }

    public static void RemoveCustomProfileImage(XIVAccount account)
    {
        foreach (var imagePath in EnumerateCustomProfileImagePaths(account))
            File.Delete(imagePath);
    }

    public static bool TryGetCustomProfileImagePath(XIVAccount account, out string imagePath)
    {
        imagePath = EnumerateCustomProfileImagePaths(account).FirstOrDefault() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(imagePath);
    }

    private static BitmapImage CreateDefaultImage()
    {
        var defaultImage = new BitmapImage(new Uri("pack://application:,,,/Resources/defaultprofile.png", UriKind.Absolute));
        defaultImage.Freeze();
        return defaultImage;
    }

    private static ImageSource LoadProfileImage(string imagePath)
    {
        using var stream  = File.OpenRead(imagePath);
        var       decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var       frame   = decoder.Frames.FirstOrDefault();
        if (frame == null)
            return DefaultImage;

        frame.Freeze();
        return frame;
    }

    private static string[] EnumerateCustomProfileImagePaths(XIVAccount account)
    {
        var profileImageDirectory = GetProfileImageDirectory();
        if (!Directory.Exists(profileImageDirectory))
            return [];

        return Directory.GetFiles(profileImageDirectory, $"{GetProfileImageFileKey(account)}.*");
    }

    private static string GetProfileImageDirectory() =>
        Path.Combine(Paths.RoamingPath, "profilePictures", "custom");

    private static string GetProfileImageFileKey(XIVAccount account) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account.ID)));
}
