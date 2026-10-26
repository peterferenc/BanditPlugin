using System;
using System.Collections;

namespace BanditPlugin.Testing
{
    /// <summary>
    /// One end-to-end scenario, run on the live server against real bandits.
    ///
    /// A test is a coroutine, so it can do what a person would do by hand - spawn something, give
    /// it an order, wait and watch - without blocking the frame it runs in. It reports failure by
    /// throwing <see cref="BanditTestFailure"/>, from anywhere in the coroutine or in anything it
    /// yields; <see cref="BanditTestRunner"/> drives the enumerators itself precisely so that a
    /// throw can be caught, which Unity's own StartCoroutine would only log and swallow.
    ///
    /// Everything a test spawns goes through its <see cref="BanditTestContext"/>, which removes it
    /// again however the test ends.
    /// </summary>
    public abstract class BanditTest
    {
        /// <summary>The word "/bandit test run" takes, and the arena point it looks for first.</summary>
        public abstract string Name { get; }

        /// <summary>One line for "/bandit test list".</summary>
        public abstract string Description { get; }

        /// <summary>
        /// A hard ceiling on the whole test, enforced by the runner on top of every timeout the test
        /// sets for itself, so a wait written without one cannot hang the suite.
        /// </summary>
        public virtual float TimeoutSeconds => 120f;

        public abstract IEnumerator Run(BanditTestContext t);
    }

    /// <summary>A test's expectation was not met. Its message is the reason shown for the failure.</summary>
    public sealed class BanditTestFailure : Exception
    {
        public BanditTestFailure(string message) : base(message)
        {
        }
    }
}
