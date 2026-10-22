import { useEffect, useRef, useState } from "react";
const port =
  location.origin === "https://ailearc.local" ? window.chrome?.webview : null;
const send = (action, values = {}) => {
  const id = crypto.randomUUID();
  port?.postMessage({ version: 1, id, action, ...values });
  return id;
};
const errors = {
  OpenFailed: "无法打开此压缩包，请检查文件是否存在或已损坏。",
  UnsupportedFormat: "暂不支持这种格式，或文件已损坏。",
  ResourceLimit: "此压缩包超过当前验证版的资源限制。",
  WorkerUnavailable: "归档 Worker 不可用，请重新构建完整桌面版本。",
  WorkerFailed: "归档处理进程意外退出，可以重新打开文件。",
  TimedOut: "读取超时，请重试。",
  ProtocolMismatch: "归档进程与界面版本不一致，请重新构建。",
};
export function useDesktop() {
  const [state, setState] = useState({
    info: null,
    page: { rows: [], total: 0, offset: 0, hasMore: false },
    pageKey: "",
    loading: false,
    querying: false,
    count: 0,
    error: "",
    passwordRequired: false,
  });
  const queryId = useRef(null),
    queryKey = useRef(""),
    revision = useRef(0);
  useEffect(() => {
    if (!port) return;
    document.title = "AileArc · 桌面只读验证";
    const receive = ({ data }) => {
      if (!data || typeof data.kind !== "string") return;
      if (data.kind === "archive") {
        revision.current++;
        queryId.current = null;
        setState((s) => ({
          ...s,
          info: { ...data, revision: revision.current },
          page: { rows: [], total: 0, offset: 0, hasMore: false },
          pageKey: "",
          loading: false,
          querying: false,
          error: "",
          passwordRequired: false,
        }));
      } else if (data.kind === "loading") {
        queryId.current = null;
        setState((s) => ({
          ...s,
          loading: true,
          count: 0,
          error: "",
          passwordRequired: false,
        }));
      } else if (data.kind === "progress")
        setState((s) => ({ ...s, count: data.count }));
      else if (data.kind === "page" && data.id === queryId.current)
        setState((s) => ({
          ...s,
          page: data.page,
          pageKey: queryKey.current,
          querying: false,
          error: "",
        }));
      else if (data.kind === "passwordRequired")
        setState((s) => ({
          ...s,
          loading: false,
          passwordRequired: true,
          error:
            data.code === "WrongPasswordOrDamaged"
              ? "密码不正确，或者文件已损坏。"
              : "",
        }));
      else if (data.kind === "passwordDismissed")
        setState((s) => ({ ...s, passwordRequired: false, error: "" }));
      else if (data.kind === "cancelled")
        setState((s) => ({
          ...s,
          loading: false,
          querying: false,
          error: s.info
            ? "已取消读取，之前打开的内容仍然保留。"
            : "已取消读取。",
        }));
      else if (
        data.kind === "error" &&
        (!data.id || data.id === queryId.current)
      )
        setState((s) => ({
          ...s,
          loading: false,
          querying: false,
          error: errors[data.code] || "读取失败，请检查文件格式或重新尝试。",
        }));
    };
    port.addEventListener("message", receive);
    send("ready");
    return () => port.removeEventListener("message", receive);
  }, []);
  return {
    ...state,
    enabled: !!port,
    pick: () => send("pickArchive"),
    cancel: () => send("cancel"),
    password: (value) => send("password", { password: value }),
    encoding: (value) => send("encoding", { codePage: value }),
    query: (values) => {
      queryKey.current = JSON.stringify(values);
      queryId.current = send("query", values);
      setState((s) => ({ ...s, querying: true, error: "" }));
    },
  };
}
