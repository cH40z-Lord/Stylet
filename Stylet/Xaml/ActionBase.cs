﻿using Stylet.Logging;
using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Data;

namespace Stylet.Xaml
{
    /// <summary>
    /// Common base class for CommandAction and EventAction
    /// </summary>
    public abstract class ActionBase : DependencyObject
    {
        private readonly ILogger logger;

        /// <summary>
        /// Gets the View to grab the View.ActionTarget from
        /// </summary>
        public DependencyObject Subject { get; private set; }

        /// <summary>
        /// Gets the method name. E.g. if someone's gone Buttom Command="{s:Action MyMethod}", this is MyMethod.
        /// </summary>
        public string MethodName { get; private set; }

        /// <summary>
        /// Gets the MethodInfo for the method to call. This has to exist, or we throw a wobbly
        /// </summary>
        protected MethodInfo TargetMethodInfo { get; private set; }

        /// <summary>
        /// Behaviour for if the target is null
        /// </summary>
        protected readonly ActionUnavailableBehaviour TargetNullBehaviour;

        /// <summary>
        /// Behaviour for if the action doesn't exist on the target
        /// </summary>
        protected readonly ActionUnavailableBehaviour ActionNonExistentBehaviour;

        /// <summary>
        /// Gets the object on which methods will be invokced
        /// </summary>
        public object Target
        {
            get { return this.GetValue(targetProperty); }
        }

        private static readonly DependencyProperty targetProperty =
            DependencyProperty.Register("target", typeof(object), typeof(ActionBase), new PropertyMetadata(null, (d, e) =>
            {
                ((ActionBase)d).UpdateActionTarget(e.OldValue, e.NewValue);
            }));

        /// <summary>
        /// Initialises a new instance of the <see cref="ActionBase"/> class
        /// </summary>
        /// <param name="subject">View to grab the View.ActionTarget from</param>
        /// <param name="methodName">Method name. the MyMethod in Buttom Command="{s:Action MyMethod}".</param>
        /// <param name="targetNullBehaviour">Behaviour for it the relevant View.ActionTarget is null</param>
        /// <param name="actionNonExistentBehaviour">Behaviour for if the action doesn't exist on the View.ActionTarget</param>
        /// <param name="logger">Logger to use</param>
        public ActionBase(DependencyObject subject, string methodName, ActionUnavailableBehaviour targetNullBehaviour, ActionUnavailableBehaviour actionNonExistentBehaviour, ILogger logger)
        {
            this.Subject = subject;
            this.MethodName = methodName;
            this.TargetNullBehaviour = targetNullBehaviour;
            this.ActionNonExistentBehaviour = actionNonExistentBehaviour;
            this.logger = logger;

            var binding = new Binding()
            {
                Path = new PropertyPath(View.ActionTargetProperty),
                Mode = BindingMode.OneWay,
                Source = this.Subject,
            };
            BindingOperations.SetBinding(this, targetProperty, binding);
        }

        private void UpdateActionTarget(object oldTarget, object newTarget)
        {
            MethodInfo targetMethodInfo = null;

            // If it's being set to the initial value, ignore it
            // At this point, we're executing the View's InitializeComponent method, and the ActionTarget hasn't yet been assigned
            // If they've opted to throw if the target is null, then this will cause that exception.
            // We'll just wait until the ActionTarget is assigned, and we're called again
            if (newTarget == View.InitialActionTarget)
            {
                return;
            }

            if (newTarget == null)
            {
                // If it's Enable or Disable we don't do anything - CanExecute will handle this
                if (this.TargetNullBehaviour == ActionUnavailableBehaviour.Throw)
                {
                    var e = new ActionTargetNullException(String.Format("ActionTarget on element {0} is null (method name is {1})", this.Subject, this.MethodName));
                    this.logger.Error(e);
                    throw e;
                }
                else
                {
                    this.logger.Info("ActionTarget on element {0} is null (method name is {1}), but NullTarget is not Throw, so carrying on", this.Subject, this.MethodName);
                }
            }
            else
            {
                var newTargetType = newTarget.GetType();

                this.OnNewNonNullTarget(newTarget, newTargetType);
                
                targetMethodInfo = newTargetType.GetMethod(this.MethodName);

                if (targetMethodInfo == null)
                {
                    if (this.ActionNonExistentBehaviour == ActionUnavailableBehaviour.Throw)
                    {
                        var e = new ActionNotFoundException(String.Format("Unable to find method {0} on {1}", this.MethodName, newTargetType.Name));
                        this.logger.Error(e);
                        throw e;
                    }
                    else
                    {
                        this.logger.Warn("Unable to find method {0} on {1}, but ActionNotFound is not Throw, so carrying on", this.MethodName, newTargetType.Name);
                    }
                }
                else
                {
                    this.AssertTargetMethodInfo(targetMethodInfo, newTargetType);
                }
            }

            this.TargetMethodInfo = targetMethodInfo;

            this.OnTargetChanged(oldTarget, newTarget);
        }

        /// <summary>
        /// Invoked when a new non-null target is set
        /// </summary>
        /// <param name="newTarget">New target</param>
        /// <param name="newTargetType">Result of newTarget.GetType()</param>
        protected internal virtual void OnNewNonNullTarget(object newTarget, Type newTargetType) { }

        /// <summary>
        /// Invoked when a new non-null target is set, which has non-null MethodInfo. Used to assert that the method signature is correct
        /// </summary>
        /// <param name="targetMethodInfo">MethodInfo of method on new target</param>
        /// <param name="newTargetType">Type of new target</param>
        protected internal abstract void AssertTargetMethodInfo(MethodInfo targetMethodInfo, Type newTargetType);

        /// <summary>
        /// Invoked when a new target is set, after all other action has been taken
        /// </summary>
        /// <param name="oldTarget">Previous target</param>
        /// <param name="newTarget">New target</param>
        protected internal virtual void OnTargetChanged(object oldTarget, object newTarget) { }

        /// <summary>
        /// Assert that the target is not View.InitialActionTarget
        /// </summary>
        protected internal void AssertTargetSet()
        {
            // If we've made it this far and the target is still the default, then something's wrong
            // Make sure they know
            if (this.Target == View.InitialActionTarget)
            {
                var ex = new ActionNotSetException(String.Format("View.ActionTarget not on control {0} (method {1}). " +
                    "This probably means the control hasn't inherited it from a parent, e.g. because a ContextMenu or Popup sits in the visual tree. " +
                    "You will need so set 's:View.ActionTarget' explicitly. See the wiki section \"Actions\" for more details.", this.Subject, this.MethodName));
                this.logger.Error(ex);
                throw ex;
            }
        }

        /// <summary>
        /// Invoke the target method with the given parameters
        /// </summary>
        /// <param name="parameters">Parameters to pass to the target method</param>
        protected internal void InvokeTargetMethod(object[] parameters)
        {
            this.logger.Info("Invoking method {0} on target {1} with parameters ({2})", this.MethodName, this.Target, parameters == null ? "none" : String.Join(", ", parameters));

            try
            {
                this.TargetMethodInfo.Invoke(this.Target, parameters);
            }
            catch (TargetInvocationException e)
            {
                // Be nice and unwrap this for them
                // They want a stack track for their VM method, not us
                this.logger.Error(e.InnerException, String.Format("Failed to invoke method {0} on target {1} with parameters ({2})", this.MethodName, this.Target, parameters == null ? "none" : String.Join(", ", parameters)));
                // http://stackoverflow.com/a/17091351/1086121
                ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            }
        }
    }
}
