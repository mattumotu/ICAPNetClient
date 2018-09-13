using System;

namespace ICAPNameSpace
{
    /// <summary>
    /// Basic Exception to use in relation with ICAP
    /// </summary>
    public class ICAPException : Exception
    {
        public ICAPException(string message)
            : base(message)
        {
        }
    }
}