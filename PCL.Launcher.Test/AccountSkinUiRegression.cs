using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Media3D;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Core.App;
using PCL.Core.Conduit;
using PCL.Core.Minecraft.Profile;
using PCL.Core.Minecraft.Profile.Models;

namespace PCL.Launcher.Test;

internal static class AccountSkinUiRegression
{
    // Run inside the existing isolated WPF Application/ConfigService test context.
    public static void Verify()
    {
        ProfileService.Load();
        var original = ProfileService.Current;
        var originalEnabled = Config.Launch.SkinPreview3D;
        var profiles = new[]
        {
            new McProfile { ProfileId = "test-microsoft", UserName = "MicrosoftPlayer", Uuid = "0123456789abcdef0123456789abcdef", ProfileType = ProfileType.Microsoft },
            new McProfile { ProfileId = "test-littleskin", UserName = "LittlePlayer", ProfileType = ProfileType.Authlib, Server = ClubCatalog.LittleSkinAuth },
            new McProfile { ProfileId = "test-mua", UserName = "MuaPlayer", ProfileType = ProfileType.Authlib, Server = ClubCatalog.MuaAuth },
            new McProfile { ProfileId = "test-offline", UserName = "OfflinePlayer", ProfileType = ProfileType.Offline }
        };
        try
        {
            foreach (var profile in profiles) ProfileService.Add(profile, select: false);
            ProfileService.Select(profiles[0]);
            var accounts = new ClubAccountPanel();
            Invoke(accounts, "RefreshAccounts");
            var grid = ((StackPanel)accounts.Content).Children.OfType<UniformGrid>().Single();
            Assert.AreEqual(2, grid.Rows);
            Assert.AreEqual(2, grid.Columns);
            CollectionAssert.AreEqual(new[] { "microsoft", "littleskin", "mua", "offline" },
                grid.Children.OfType<Button>().Select(b => (string)b.Tag).ToArray());
            foreach (var button in grid.Children.OfType<Button>()) StringAssert.Contains((string)button.Content, "(1)");

            Config.Launch.SkinPreview3D = false;
            var skinPage = new PageLoginProfileSkin();
            Invoke(skinPage, "RefreshPreviewOptions");
            var menu = ((MyIconButton)skinPage.FindName("BtnSkin")).ContextMenu;
            var allow = menu.Items.OfType<MyMenuItem>().Single(m => Equals(m.Tag, "preview3d-allowed"));
            var preview = menu.Items.OfType<MyMenuItem>().Single(m => Equals(m.Tag, "preview3d"));
            Assert.IsTrue(allow.IsCheckable);
            Assert.IsFalse(preview.IsEnabled);
            Config.Launch.SkinPreview3D = true;
            Invoke(skinPage, "RefreshPreviewOptions");
            Assert.IsTrue(allow.IsChecked);
            Assert.IsTrue(preview.IsEnabled);
            ProfileService.Select(profiles[3]);
            Invoke(skinPage, "RefreshPreviewOptions");
            Assert.IsFalse(preview.IsEnabled, "Offline profiles must not open the Microsoft preview.");

            var data = new SkinPreviewData(SkinModelTest.Texture(64, 64), SkinModelTest.Texture(64, 32), true);
            string? loadedName = null;
            var popup = new ClubSkinPreviewWindow(profiles[0], (snapshot, _) =>
            {
                loadedName = snapshot.UserName;
                Assert.IsEmpty(snapshot.AccessToken);
                return Task.FromResult(data);
            });
            profiles[0].UserName = "ChangedAfterOpening";
            var host = new Grid { Width = 660, Height = 460 };
            host.Children.Add(popup);
            host.Measure(new Size(660, 460));
            host.Arrange(new Rect(0, 0, 660, 460));
            popup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            host.InvalidateMeasure();
            host.InvalidateArrange();
            host.Measure(new Size(660, 460));
            host.Arrange(new Rect(0, 0, 660, 460));
            host.UpdateLayout();
            Assert.AreEqual("MicrosoftPlayer", loadedName, "The popup must keep the original profile snapshot.");
            Assert.IsTrue(popup.ActualHeight + popup.Margin.Top + popup.Margin.Bottom <= host.ActualHeight,
                $"The popup and its margins must fit the real host height (popup={popup.ActualHeight}, host={host.ActualHeight}, max={popup.MaxHeight}).");
            var visual = (ModelVisual3D)popup.FindName("PlayerModel");
            Assert.IsNotNull(visual.Content);
            Assert.IsTrue(((CheckBox)popup.FindName("CapeToggle")).IsEnabled);
            StringAssert.Contains(((TextBlock)popup.FindName("StatusText")).Text, "已加载当前披风");
            var capeDepth = visual.Content.Bounds.Z;
            ((CheckBox)popup.FindName("CapeToggle")).IsChecked = false;
            Assert.IsTrue(visual.Content.Bounds.Z > capeDepth, "Turning off the cape must remove its geometry.");
            popup.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));

            CancellationToken pendingToken = default;
            var pending = new TaskCompletionSource<SkinPreviewData>();
            var cancelledPopup = new ClubSkinPreviewWindow(profiles[0], (_, token) =>
            {
                pendingToken = token;
                return pending.Task;
            });
            cancelledPopup.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            cancelledPopup.ClosePreview();
            Assert.IsTrue(pendingToken.IsCancellationRequested, "Closing the popup must cancel texture loading.");
            pending.SetCanceled(pendingToken);
        }
        finally
        {
            Config.Launch.SkinPreview3D = originalEnabled;
            foreach (var profile in profiles) ProfileService.Remove(profile);
            ProfileService.Select(original);
        }
    }

    private static void Invoke(object target, string method)
        => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
}
