using System;
using System.Runtime.Serialization;

namespace ICAPNetClient
{
    /// <summary>
    /// Basic Exception to use in relation with ICAP
    /// </summary>
    [Serializable]
    public class ICAPException : Exception
    {
        public ICAPException()
        {
        }

        public ICAPException(string message)
            : base(message)
        {
        }

        public ICAPException(string message, Exception inner)
            : base(message, inner)
        {
        }

        // Without this constructor, deserialization will fail
        protected ICAPException(SerializationInfo info, StreamingContext context)
            : base(info, context)
        {
        }
    }
}