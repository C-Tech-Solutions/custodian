using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Custodian.App.Services;

namespace Custodian.Tests;

public sealed class UpdateInteractionShieldControllerTests
{
    [Fact]
    public void BeginUpdateAndEnd_ManageShieldWithoutDisablingContent()
    {
        RunInSta(() =>
        {
            var shield = new Border { Visibility = Visibility.Collapsed };
            var title = new TextBlock();
            var detail = new TextBlock();
            var unrelatedList = new ListBox();
            var unrelatedTree = new TreeView();
            var controller = new UpdateInteractionShieldController(shield, title, detail);

            controller.Begin("Verifying update...", "Checking signatures.");

            Assert.True(controller.IsActive);
            Assert.Equal(Visibility.Visible, shield.Visibility);
            Assert.Equal("Verifying update...", title.Text);
            Assert.Equal("Checking signatures.", detail.Text);
            Assert.True(unrelatedList.IsEnabled);
            Assert.True(unrelatedTree.IsEnabled);

            controller.UpdateMessage("Installing update...", "Handing off to the updater.");

            Assert.Equal("Installing update...", title.Text);
            Assert.Equal("Handing off to the updater.", detail.Text);

            controller.End();

            Assert.False(controller.IsActive);
            Assert.Equal(Visibility.Collapsed, shield.Visibility);
            Assert.True(unrelatedList.IsEnabled);
            Assert.True(unrelatedTree.IsEnabled);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
