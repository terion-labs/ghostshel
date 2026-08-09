namespace Exclr8Cef.Native;

internal unsafe partial struct excef_init_settings
{
    [NativeTypeName("const char *")]
    public sbyte* cache_path;

    [NativeTypeName("const char *")]
    public sbyte* root_cache_path;

    [NativeTypeName("const char *")]
    public sbyte* user_agent;

    [NativeTypeName("const char *")]
    public sbyte* user_agent_product;

    [NativeTypeName("const char *")]
    public sbyte* locale;

    [NativeTypeName("const char *")]
    public sbyte* accept_language_list;

    [NativeTypeName("const char *")]
    public sbyte* log_file;

    [NativeTypeName("const char *")]
    public sbyte* javascript_flags;

    public int log_severity;

    public int persist_session_cookies;

    public int remote_debugging_port;

    public int enable_javascript_bridge;
}
