using DOL.Events;
using NUnit.Framework;

namespace DOL.UnitTests
{
    [TestFixture]
    public class UT_EventHandlerChain
    {
        [Test]
        public void RemovingAnAbsentHandlerLeavesOtherSubscribersIntact()
        {
            DOLEventHandler first = (_, _, _) => { };
            DOLEventHandler second = (_, _, _) => { };
            DOLEventHandler missing = (_, _, _) => { };
            var tail = DOLEventHandlerChain.Subscribe(null, first);
            var chain = DOLEventHandlerChain.Subscribe(tail, second);

            Assert.That(DOLEventHandlerChain.Unsubscribe(chain, missing), Is.SameAs(chain));
            Assert.That(DOLEventHandlerChain.Unsubscribe(chain, second), Is.SameAs(tail));
            Assert.That(DOLEventHandlerChain.Unsubscribe(tail, first), Is.Null);
            Assert.That(DOLEventHandlerChain.Unsubscribe(null, first), Is.Null);
        }
    }
}
