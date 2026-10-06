using NUnit.Framework;

namespace Cubeglass.Unity.Input.Tests
{
    /// <summary>
    /// Pins the boot-time cursor policy (S7 HIL defect, 2026-10-06). In the
    /// first on-glasses player run the OS cursor lived on the laptop display
    /// while the game ran fullscreen on the glasses, so the legacy mouse path
    /// (<c>Input.GetMouseButton(0/1)</c> and mouse look) never reached the game
    /// window: keyboard kept working because focus follows the fullscreen
    /// window, but break/place/look were dead. The player must lock and hide
    /// the cursor; the Editor preview never locks (PlayMode tests and the
    /// windowed preview keep a usable pointer).
    /// </summary>
    public class GameBootCursorTests
    {
        [Test]
        public void PlayerWithLockingEnabledLocksTheCursor()
        {
            Assert.IsTrue(GameBoot.ShouldLockCursor(true, false), "a player must lock the cursor");
        }

        [Test]
        public void EditorNeverLocksTheCursor()
        {
            Assert.IsFalse(GameBoot.ShouldLockCursor(true, true), "the editor preview keeps a usable pointer");
        }

        [Test]
        public void DisabledLockingNeverLocks()
        {
            Assert.IsFalse(GameBoot.ShouldLockCursor(false, false), "the seam can disable locking in a player");
            Assert.IsFalse(GameBoot.ShouldLockCursor(false, true), "disabled locking is also off in the editor");
        }
    }
}
