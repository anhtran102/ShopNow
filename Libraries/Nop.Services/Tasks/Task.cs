﻿using System;
using Nop.Core.Caching;
using Nop.Core.Configuration;
using Nop.Core.Domain.Tasks;
using Nop.Core.Infrastructure;
using Nop.Services.Logging;

namespace Nop.Services.Tasks
{
    /// <summary>
    /// Task
    /// </summary>
    public partial class Task
    {
        #region Fields

        private bool? _enabled;

        #endregion

        #region Ctor

        /// <summary>
        /// Ctor for Task
        /// </summary>
        /// <param name="task">Task </param>
        public Task(ScheduleTask task)
        {
            ScheduleTask = task;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Initialize and execute task
        /// </summary>
        private void ExecuteTask()
        {
            var scheduleTaskService = EngineContext.Current.Resolve<IScheduleTaskService>();
            
            if (!Enabled)
                return;

            var type = Type.GetType(ScheduleTask.Type);
            if (type == null)
                throw new Exception($"Schedule task ({ScheduleTask.Type}) cannot by instantiated");

            object instance = null;
            try
            {
                instance = EngineContext.Current.Resolve(type);
            }
            catch
            {
                //try resolve
            }
            if (instance == null)
            {
                //not resolved
                instance = EngineContext.Current.ResolveUnregistered(type);
            }

            var task = instance as IScheduleTask;
            if (task == null)
                return;

            ScheduleTask.LastStartUtc = DateTime.UtcNow;
            //update appropriate datetime properties
            scheduleTaskService.UpdateTask(ScheduleTask);
            task.Execute();
            ScheduleTask.LastEndUtc = ScheduleTask.LastSuccessUtc = DateTime.UtcNow;
            //update appropriate datetime properties
            scheduleTaskService.UpdateTask(ScheduleTask);
        }

        protected virtual bool IsTaskAlreadyRunning(ScheduleTask scheduleTask)
        {
            //task run for the first time
            if (!scheduleTask.LastStartUtc.HasValue && !scheduleTask.LastEndUtc.HasValue)
                return false;

            var lastStartUtc = scheduleTask.LastStartUtc ?? DateTime.UtcNow;

            //task already finished
            if (scheduleTask.LastEndUtc.HasValue && lastStartUtc < scheduleTask.LastEndUtc)
                return false;

            //task wasn't finished last time
            if (lastStartUtc.AddSeconds(scheduleTask.Seconds) <= DateTime.UtcNow)
                return false;

            return true;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Executes the task
        /// </summary>
        /// <param name="throwException">A value indicating whether exception should be thrown if some error happens</param>
        /// <param name="ensureRunOncePerPeriod">A value indicating whether we should ensure this task is run once per run period</param>
        public void Execute(bool throwException = false, bool ensureRunOncePerPeriod = true)
        {
            if (ScheduleTask == null || !Enabled)
                return;

            if (ensureRunOncePerPeriod)
            {
                //task already running
                if (IsTaskAlreadyRunning(ScheduleTask))
                    return;

                //validation (so nobody else can invoke this method when he wants)
                if (ScheduleTask.LastEndUtc.HasValue && (DateTime.UtcNow - ScheduleTask.LastEndUtc).Value.TotalSeconds <
                    ScheduleTask.Seconds)
                    //too early
                    return;
            }

            try
            {
                var nopConfig = EngineContext.Current.Resolve<NopConfig>();
                if (nopConfig.RedisCachingEnabled)
                {
                    //get expiration time
                    var expirationInSeconds = ScheduleTask.Seconds <= 300 ? ScheduleTask.Seconds - 1 : 300;

                    //execute task with lock
                    var redisWrapper = EngineContext.Current.Resolve<IRedisConnectionWrapper>();
                    redisWrapper.PerformActionWithLock(ScheduleTask.Type, TimeSpan.FromSeconds(expirationInSeconds), ExecuteTask);
                }
                else
                {
                    ExecuteTask();
                }
            }
            catch (Exception exc)
            {
                var scheduleTaskService = EngineContext.Current.Resolve<IScheduleTaskService>();

                ScheduleTask.Enabled = !ScheduleTask.StopOnError;
                ScheduleTask.LastEndUtc = DateTime.UtcNow;
                scheduleTaskService.UpdateTask(ScheduleTask);

                //log error
                var logger = EngineContext.Current.Resolve<ILogger>();
                logger.Error($"Error while running the '{ScheduleTask.Name}' schedule task. {exc.Message}", exc);
                if (throwException)
                    throw;
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Schedule task
        /// </summary>
        public ScheduleTask ScheduleTask { get; }

        /// <summary>
        /// A value indicating whether the task is enabled
        /// </summary>
        public bool Enabled
        {
            get
            {
                if (!_enabled.HasValue)
                    _enabled = ScheduleTask?.Enabled;

                    return _enabled.HasValue && _enabled.Value;
            }
            set => _enabled = value;
        }

        #endregion
    }
}