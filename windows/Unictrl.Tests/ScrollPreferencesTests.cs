using Xunit;
namespace Unictrl.Tests;
public class ScrollPreferencesTests
{
    [Fact] public void InvalidRemotePreferencesAreRejected()
    {
        Assert.Null(ScrollPreferences.Parse([2,1,1])); Assert.Null(ScrollPreferences.Parse([1,2,0])); Assert.Null(ScrollPreferences.Parse([1,1]));
        var edit = new ScrollPreferences(true,false).Message(0x38); Assert.Equal(new byte[]{0x38,1,1,0},edit);
        Assert.Equal(new ScrollPreferences(true,false),ScrollPreferences.Parse(edit.AsSpan(1)));
    }
}
