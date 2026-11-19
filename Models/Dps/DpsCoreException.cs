namespace TrustNTE.Models.Dps
{
    /// <summary>Core JSON-RPC 错误；调用方按稳定 domain_code 分支。</summary>
    public sealed class DpsCoreException : Exception
    {
        public int RpcCode { get; }
        public string DomainCode { get; }

        public DpsCoreException(int rpcCode, string domainCode, string message)
            : base(message)
        {
            RpcCode = rpcCode;
            DomainCode = domainCode;
        }

        public DpsCoreException(string domainCode, string message)
            : base(message)
        {
            RpcCode = -32000;
            DomainCode = domainCode;
        }
    }
}
