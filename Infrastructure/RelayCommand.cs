using System;
using System.Windows.Input;

namespace PackageManager.Models
{
    /// <summary>
    /// 简单的 RelayCommand 实现。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action execute;

        private readonly Func<bool> canExecute;

        /// <summary>
        /// 初始化 <see cref="RelayCommand"/> 的新实例。
        /// </summary>
        /// <param name="execute">执行的操作。</param>
        /// <param name="canExecute">判断是否可执行的函数，为 null 时始终可执行。</param>
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            this.execute = execute ?? throw new ArgumentNullException(nameof(execute));
            this.canExecute = canExecute;
        }

        /// <summary>
        /// 当影响命令是否应执行的条件发生更改时触发。
        /// </summary>
        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;

            remove => CommandManager.RequerySuggested -= value;
        }

        /// <summary>
        /// 判断当前命令是否可以执行。
        /// </summary>
        /// <param name="parameter">命令参数（未使用）。</param>
        /// <returns>如果可以执行返回 true，否则返回 false。</returns>
        public bool CanExecute(object parameter)
        {
            return canExecute?.Invoke() ?? true;
        }

        /// <summary>
        /// 执行命令。
        /// </summary>
        /// <param name="parameter">命令参数（未使用）。</param>
        public void Execute(object parameter)
        {
            execute();
        }
    }
}
