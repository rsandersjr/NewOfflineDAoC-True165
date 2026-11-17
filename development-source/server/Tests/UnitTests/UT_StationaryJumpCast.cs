using DOL.GS.PacketHandler.Client.v168;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_StationaryJumpCast
    {
        [TestCase(true, false, 180f, 7, true)]
        [TestCase(false, false, 140f, 7, false)]
        [TestCase(true, true, 0f, 7, false)]
        [TestCase(true, false, -80f, -7, false)]
        [TestCase(false, true, -180f, -7, false)]
        [TestCase(true, false, 180f, 0, false)]
        public void OnlyARisingGroundToAirTransitionIsATakeoff(
            bool wasGrounded, bool grounded, float verticalSpeed, int rise, bool expected)
        {
            Assert.That(PlayerPositionUpdateHandler.IsJumpTakeoff(wasGrounded, grounded, verticalSpeed, rise),
                Is.EqualTo(expected));
        }
    }
}
