﻿using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Griffin.Signals
{
    /// <summary>
    ///     Signals can be used to keep track of states in the application and to allow you to avoid spamming the log file
    /// </summary>
    public class Signal
    {
        private static readonly SignalManager _signalManager = new SignalManager();
        private bool _disabled;
        private string _message;
        private int _raiseCountSinceLastReset;

        internal Signal(string name)
        {
            if (name == null) throw new ArgumentNullException("name");
            Name = name;
            IdleSinceUtc = DateTime.UtcNow;
            Properties = new Dictionary<string, string>();
        }

        /// <summary>
        ///     When the signal was moved into non-signaled state.
        /// </summary>
        public DateTime IdleSinceUtc { get; private set; }

        /// <summary>
        ///     Signal name
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        ///     Signal have been raised.
        /// </summary>
        public bool IsRaised { get; private set; }

        /// <summary>
        ///     When the raised signaled is automatically suppressed again
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Allows you to specify an amount of time
        ///     </para>
        /// </remarks>
        public TimeSpan Expiration { get; set; }

        /// <summary>
        ///     Kind of signal
        /// </summary>
        public SignalKind Kind { get; set; }

        /// <summary>
        ///     When the signal was raised
        /// </summary>
        /// <value>
        ///     <c>DateTime.MinValue</c> if it's not raised.
        /// </value>
        public DateTime RaisedSinceUtc { get; private set; }

        /// <summary>
        ///     Allows you to attach state etc
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Typically used when you want to do additional filtering in the server that receives signal changes.
        ///     </para>
        /// </remarks>
        public IDictionary<string, string> Properties { get; private set; }

        /// <summary>
        ///     A token which you can use to keep track of if the signal should be raised or not.
        /// </summary>
        /// <example>
        ///     <code>
        /// var mySignal = 
        /// 
        /// 
        /// </code>
        /// </example>
        public object UserToken { get; set; }

        /// <summary>
        ///     AMount of times that <c>Raise()</c> have been invoked since last reset.
        /// </summary>
        public int RaiseCountSinceLastReset
        {
            get { return _raiseCountSinceLastReset; }
        }

        /// <summary>
        ///     One of the signals was raised.
        /// </summary>
        public static event EventHandler<SignalRaisedEventArgs> SignalRaised = delegate { };

        /// <summary>
        ///     One of the signals was reset
        /// </summary>
        public static event EventHandler<SignalSuppressedEventArgs> SignalSuppressed = delegate { };

        /// <summary>
        ///     Signal was raised.
        /// </summary>
        public event EventHandler<SignalRaisedEventArgs> Raised = delegate { };

        /// <summary>
        ///     Signal was suppressed.
        /// </summary>
        public event EventHandler<SignalSuppressedEventArgs> Suppressed = delegate { };

        /// <summary>
        ///     Create a new signal
        /// </summary>
        /// <param name="signalName">Must be unique in the application</param>
        /// <returns>Created signal</returns>
        /// <remarks>
        /// </remarks>
        /// <exception cref="InvalidOperationException">A signal with that name have already been added.</exception>
        public static Signal Create(string signalName)
        {
            if (signalName == null) throw new ArgumentNullException("signalName");

            var signal = new Signal(signalName);
            if (!_signalManager.TryAdd(signalName, signal))
                throw new InvalidOperationException("A signal with name '" + signalName + "' have already been added.");

            signal.Raised += OnTriggerGlobalRaise;
            signal.Suppressed += OnTriggerGlobalSuppress;
            return signal;
        }

        /// <summary>
        ///     Create a new signal
        /// </summary>
        /// <param name="signalName">Must be unique in the application</param>
        /// <param name="kind">Indicates if this signal represents that something is working or failing.</param>
        /// <returns>Created signal</returns>
        /// <remarks>
        /// </remarks>
        /// <exception cref="InvalidOperationException">A signal with that name have already been added.</exception>
        public static Signal Create(string signalName, SignalKind kind)
        {
            if (signalName == null) throw new ArgumentNullException("signalName");

            var signal = new Signal(signalName);
            if (!_signalManager.TryAdd(signalName, signal))
                throw new InvalidOperationException("A signal with name '" + signalName + "' have already been added.");

            signal.Kind = kind;
            signal.Raised += OnTriggerGlobalRaise;
            signal.Suppressed += OnTriggerGlobalSuppress;
            return signal;
        }

        /// <summary>
        ///     Disable notifications for this signal
        /// </summary>
        public void Disable()
        {
            _disabled = true;
        }

        /// <summary>
        ///     Enable notifications for this signal (if you have previously called <c>Disable()</c>).
        /// </summary>
        public void Enable()
        {
            _disabled = false;
        }


        /// <summary>
        ///     Just a typed accessor for the <see cref="UserToken" />.
        /// </summary>
        /// <typeparam name="T">Type to cast to</typeparam>
        /// <returns>Casted token</returns>
        public T GetUserToken<T>()
        {
            return (T) UserToken;
        }

        /// <summary>
        ///     Raise a signal.
        /// </summary>
        /// <param name="signalName">Name of the signal</param>
        /// <param name="reason">Reason to why we are raising the signal</param>
        /// <param name="reasonFormatters">Formatters for <paramref name="reason" /> (like <c>string.Format()</c>)</param>
        /// <returns><c>true</c> if the signal was not already raised; <c>false</c> if it's already in signaled state.</returns>
        /// <remarks>
        ///     <para>
        ///         Will create a new signal if no one have been registered with the specified name.
        ///     </para>
        /// </remarks>
        public static bool Raise(string signalName, string reason, params object[] reasonFormatters)
        {
            var msg = string.Format(reason, reasonFormatters);
            var signal = _signalManager.GetOrAdd(signalName, name =>
            {
                var x = new Signal(signalName);
                x.Raised += OnTriggerGlobalRaise;
                x.Suppressed += OnTriggerGlobalSuppress;
                return x;
            });

            return signal.Raise(msg);
        }

        /// <summary>
        ///     Configure a signal
        /// </summary>
        /// <param name="signalName">Name of signal</param>
        /// <param name="configureMethod">Used to configure the signal</param>
        /// <param name="addIfMissing">Create it if it's missing.</param>
        public static void Configure(string signalName, Action<Signal> configureMethod, bool addIfMissing = false)
        {
            Signal signal;
            if (addIfMissing)
            {
                signal = _signalManager.GetOrAdd(signalName, name => new Signal(name));
            }
            else
            {
                if (!_signalManager.TryGet(signalName, out signal))
                    return;
            }

            configureMethod(signal);
        }

        /// <summary>
        ///     Raise a signal.
        /// </summary>
        /// <param name="signalName">Name of the signal</param>
        /// <param name="reason">Reason to why we are raising the signal</param>
        /// <param name="exception">Exception that caused the signal to be raised</param>
        /// <returns><c>true</c> if the signal was not already raised; <c>false</c> if it's already in signaled state.</returns>
        /// <remarks>
        ///     <para>
        ///         Will create a new signal if no one have been registered with the specified name.
        ///     </para>
        /// </remarks>
        public static bool Raise(string signalName, string reason, Exception exception)
        {
            if (signalName == null) throw new ArgumentNullException("signalName");
            if (reason == null) throw new ArgumentNullException("reason");
            if (exception == null) throw new ArgumentNullException("exception");
            var signal = _signalManager.GetOrAdd(signalName, name =>
            {
                var x = new Signal(signalName);
                x.Raised += OnTriggerGlobalRaise;
                x.Suppressed += OnTriggerGlobalSuppress;
                return x;
            });

            return signal.Raise(reason, exception);
        }

        /// <summary>
        ///     Raise a signal.
        /// </summary>
        /// <param name="reason">Reason to why we are raising the signal</param>
        /// <param name="reasonFormatters">Formatters for <paramref name="reason" /> (like <c>string.Format()</c>)</param>
        /// <returns><c>true</c> if the signal was not already raised; <c>false</c> if it's already in signaled state.</returns>
        /// <remarks>
        ///     <para>
        ///         Will create a new signal if no one have been registered with the specified name.
        ///     </para>
        /// </remarks>
        public bool Raise(string reason, params object[] reasonFormatters)
        {
            if (reason == null) throw new ArgumentNullException("reason");
            if (_disabled)
                return false;


            lock (this)
            {
                _raiseCountSinceLastReset = RaiseCountSinceLastReset + 1;

                if (IsRaised)
                    return false;

                var callFrame = new StackFrame(1);
                var caller = callFrame.GetMethod().DeclaringType.FullName + "." + callFrame.GetMethod().Name;
                RaisedSinceUtc = DateTime.UtcNow;
                IdleSinceUtc = DateTime.MinValue;
                IsRaised = true;
                _message = string.Format(reason, reasonFormatters);
                TriggerRaised(_message, caller);
            }

            return true;
        }

        /// <summary>
        ///     Raise a signal.
        /// </summary>
        /// <param name="reason">Reason to why we are raising the signal</param>
        /// <param name="exception">Exception that caused the signal to be raised.</param>
        /// <returns><c>true</c> if the signal was not already raised; <c>false</c> if it's already in signaled state.</returns>
        /// <remarks>
        ///     <para>
        ///         Will create a new signal if no one have been registered with the specified name.
        ///     </para>
        /// </remarks>
        public bool Raise(string reason, Exception exception)
        {
            if (reason == null) throw new ArgumentNullException("reason");
            if (exception == null) throw new ArgumentNullException("exception");
            if (_disabled)
                return false;

            lock (this)
            {
                _raiseCountSinceLastReset = RaiseCountSinceLastReset + 1;

                if (IsRaised)
                    return false;

                var callFrame = new StackFrame(1);
                var caller = callFrame.GetMethod().DeclaringType.FullName + "." + callFrame.GetMethod().Name;
                RaisedSinceUtc = DateTime.UtcNow;
                IdleSinceUtc = DateTime.MinValue;
                IsRaised = true;
                _message = reason;
                TriggerRaised(reason, caller, exception);
                return true;
            }
        }

        private void TriggerRaised(string msg, string caller, Exception exception = null)
        {
            if (Raised == null)
                return;

            if (exception == null)
                Raised(this, new SignalRaisedEventArgs(Name, msg, caller));
            else
                Raised(this, new SignalRaisedEventArgs(Name, msg, caller, exception));
        }

        /// <summary>
        ///     Transition signal to non-signaled state.
        /// </summary>
        /// <param name="signalName">Signal to reset</param>
        /// <returns><c>true</c> if transition was made; <c>false</c> if the signal already was in non-signaled state.</returns>
        public static bool Reset(string signalName)
        {
            if (signalName == null) throw new ArgumentNullException("signalName");
            Signal signal;
            if (!_signalManager.TryGet(signalName, out signal))
                return false;

            return signal.Reset();
        }

        /// <summary>
        ///     Transition signal to non-signaled state.
        /// </summary>
        /// <returns><c>true</c> if transition was made; <c>false</c> if the signal already was in non-signaled state.</returns>
        public bool Reset()
        {
            lock (this)
            {
                if (!IsRaised)
                    return false;

                var callFrame = new StackFrame(1);
                var caller = callFrame.GetMethod().DeclaringType.FullName + "." + callFrame.GetMethod().Name;
                TriggerSuppressed(caller);
                IsRaised = false;
                _raiseCountSinceLastReset = 0;
                RaisedSinceUtc = DateTime.MinValue;
                IdleSinceUtc = DateTime.UtcNow;
                return true;
            }
        }

        /// <summary>
        ///     Do not check signals for expirations
        /// </summary>
        public static void SuspendExpirations()
        {
            _signalManager.SuspendExpirations();
        }

        /// <summary>
        ///     Been signaled the configured amount of time, transition to non-signaled.
        /// </summary>
        internal void Expire()
        {
            lock (this)
            {
                if (!IsRaised)
                    return;

                IsRaised = false;
                TriggerSuppressed("Signal.Expire()", true);
                RaisedSinceUtc = DateTime.MinValue;
                _raiseCountSinceLastReset = 0;
                IdleSinceUtc = DateTime.UtcNow;
            }
        }

        private void TriggerSuppressed(string callingMethod, bool automated = false)
        {
            if (Suppressed != null)
                Suppressed(this, new SignalSuppressedEventArgs(Name, callingMethod) {Automated = automated});
        }

        internal static void OnTriggerGlobalRaise(object sender, SignalRaisedEventArgs e)
        {
            if (SignalRaised != null)
                SignalRaised(sender, e);
        }

        internal static void OnTriggerGlobalSuppress(object sender, SignalSuppressedEventArgs e)
        {
            if (SignalSuppressed != null)
                SignalSuppressed(sender, e);
        }

        internal static void ClearForTests()
        {
            _signalManager.Clear();
        }
    }
}