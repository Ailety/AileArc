import React, { useState, useMemo, useEffect, useRef } from "react";
import {
  Archive,
  ArrowLeft,
  ArrowRight,
  ArrowUp,
  ArrowDown,
  CaretDown,
  CaretRight,
  Check,
  CheckCircle,
  Copy,
  DotsThree,
  File,
  FileCode,
  FilePdf,
  FileText,
  Folder,
  FolderOpen,
  Info,
  MagnifyingGlass,
  Package,
  Palette,
  Plus,
  SidebarSimple,
  X,
  XCircle,
  ArrowSquareOut,
  ClockCounterClockwise,
} from "@phosphor-icons/react";
import { files, archives, typeName, size } from "./data";
import {
  archiveName,
  nameError,
  destinationError,
  extractionTargets,
} from "./workflow";

function FileIcon({ file, large = false }) {
  const Icon =
    file.type === "folder"
      ? Folder
      : file.type === "pdf"
        ? FilePdf
        : file.type === "code"
          ? FileCode
          : file.type === "md"
            ? FileText
            : File;
  return (
    <Icon
      className={`file-icon ${file.type}`}
      size={large ? 58 : 21}
      weight={large ? "light" : "regular"}
      aria-hidden="true"
    />
  );
}
function IconButton({ label, children, ...props }) {
  return (
    <button className="icon-button" aria-label={label} title={label} {...props}>
      {children}
    </button>
  );
}
function Checkbox({ mixed = false, ...props }) {
  const ref = useRef();
  useEffect(() => {
    ref.current.indeterminate = mixed;
  }, [mixed]);
  return <input type="checkbox" ref={ref} {...props} />;
}
function Modal({ title, onClose, children }) {
  const ref = useRef();
  useEffect(() => {
    const before = document.activeElement;
    const el = ref.current;
    el.showModal();
    return () => {
      el.close();
      before?.focus();
    };
  }, []);
  return (
    <dialog
      ref={ref}
      aria-labelledby="dialog-title"
      onCancel={(e) => {
        e.preventDefault();
        onClose();
      }}
      onClick={(e) => {
        if (e.target === ref.current) onClose();
      }}
    >
      <header>
        <h2 id="dialog-title">{title}</h2>
        <IconButton label="关闭对话框" onClick={onClose}>
          <X />
        </IconButton>
      </header>
      {children}
    </dialog>
  );
}

export function App() {
  const [archive, setArchive] = useState(archives[0]),
    [path, setPath] = useState("docs/"),
    [history, setHistory] = useState([]),
    [future, setFuture] = useState([]),
    [search, setSearch] = useState("");
  const [selection, setSelection] = useState(["docs/Getting Started.pdf"]),
    [anchor, setAnchor] = useState("docs/Getting Started.pdf"),
    [sort, setSort] = useState({ key: "name", reverse: false });
  const [theme, setTheme] = useState("graphite"),
    [appearance, setAppearance] = useState(false),
    [inspector, setInspector] = useState(true),
    [compact, setCompact] = useState(false),
    [tree, setTree] = useState(true),
    [menu, setMenu] = useState(false);
  const [modal, setModal] = useState(null),
    [toast, setToast] = useState(""),
    [job, setJob] = useState(null),
    [extraction, setExtraction] = useState([]);
  const [destination, setDestination] = useState("D:\\Downloads\\project-kit"),
    [conflict, setConflict] = useState("ask"),
    [keepPaths, setKeepPaths] = useState(true),
    [openAfter, setOpenAfter] = useState(false);
  const [createName, setCreateName] = useState("Archive"),
    [format, setFormat] = useState("zip"),
    [sources, setSources] = useState([]),
    [encoding, setEncoding] = useState("自动");
  const searchRef = useRef(),
    rowRefs = useRef(new Map()),
    toastTimer = useRef(),
    running = useRef();
  const visible = useMemo(
    () =>
      files
        .filter((f) =>
          search
            ? (f.parent + f.name).toLowerCase().includes(search.toLowerCase())
            : f.parent === path,
        )
        .sort((a, b) => {
          if (a.type === "folder" && b.type !== "folder") return -1;
          if (b.type === "folder" && a.type !== "folder") return 1;
          const v =
            sort.key === "size"
              ? a.size - b.size
              : String(a[sort.key]).localeCompare(String(b[sort.key]), "en");
          return sort.reverse ? -v : v;
        }),
    [search, path, sort],
  );
  const selected = files.filter((f) => selection.includes(f.id)),
    file = selected.length === 1 ? selected[0] : null;
  const all =
    visible.length > 0 && visible.every((f) => selection.includes(f.id));
  const busy = job && job.progress < 100 && !job.cancelled;
  const previewTargets = extractionTargets(extraction, destination, keepPaths);
  const outputName = archiveName(createName, format);
  const currentView = () => ({ path, search, selection, anchor, sort });
  const restoreView = (view) => {
    setPath(view.path);
    setSearch(view.search);
    setSelection(view.selection);
    setAnchor(view.anchor);
    setSort(view.sort);
    requestAnimationFrame(() =>
      rowRefs.current.get(view.selection[0])?.focus(),
    );
  };
  const notify = (text) => {
    clearTimeout(toastTimer.current);
    setToast(text);
    toastTimer.current = setTimeout(() => setToast(""), 4200);
  };
  const go = (next) => {
    if (path === next && !search) return;
    setHistory((h) => [...h, currentView()]);
    setFuture([]);
    setPath(next);
    setSearch("");
    setSelection([]);
    setAnchor(null);
  };
  const back = () => {
    if (!history.length) return;
    const prev = history.at(-1);
    setHistory((h) => h.slice(0, -1));
    setFuture((h) => [...h, currentView()]);
    restoreView(prev);
  };
  const forward = () => {
    if (!future.length) return;
    setHistory((h) => [...h, currentView()]);
    restoreView(future.at(-1));
    setFuture((h) => h.slice(0, -1));
  };
  const openArchive = (name) => {
    setArchive(name);
    setPath("docs/");
    setHistory([]);
    setFuture([]);
    setSearch("");
    setSelection([]);
    setModal(null);
    notify(`已打开演示压缩包 ${name}`);
  };
  const toggle = (f, e, only = false) => {
    if (e.shiftKey && anchor) {
      const a = visible.findIndex((x) => x.id === anchor),
        b = visible.findIndex((x) => x.id === f.id);
      if (a >= 0) {
        const range = visible
          .slice(Math.min(a, b), Math.max(a, b) + 1)
          .map((x) => x.id);
        setSelection(
          e.ctrlKey || e.metaKey
            ? [...new Set([...selection, ...range])]
            : range,
        );
        return;
      }
    }
    setSelection(
      only && !e.ctrlKey && !e.metaKey
        ? [f.id]
        : selection.includes(f.id)
          ? selection.filter((id) => id !== f.id)
          : [...selection, f.id],
    );
    setAnchor(f.id);
  };
  const selectAll = () =>
    setSelection(
      all
        ? selection.filter((id) => !visible.some((f) => f.id === id))
        : [...new Set([...selection, ...visible.map((f) => f.id)])],
    );
  const openFile = (f) =>
    f.type === "folder"
      ? go(f.parent + f.name + "/")
      : notify(`原型演示：将使用 Windows 默认应用打开 ${f.name}`);
  const showExtract = (whole = false) => {
    if (busy) {
      notify("请等待当前模拟任务完成，或先取消它。");
      return;
    }
    setExtraction(whole ? files.filter((f) => !f.parent) : selected);
    setModal("extract");
    setMenu(false);
  };
  const runJob = (kind, count, context) => {
    if (busy) return;
    setModal(null);
    clearInterval(running.current);
    const task = {
      kind,
      count,
      context,
      openAfter: kind === "解压" && openAfter,
      cancelled: false,
    };
    setJob({ ...task, progress: 0 });
    let progress = 0;
    running.current = setInterval(() => {
      progress += 20;
      setJob({ ...task, progress });
      if (progress >= 100) {
        clearInterval(running.current);
        notify(`模拟${kind}完成 · ${context}，未实际读写文件`);
      }
    }, 650);
  };
  useEffect(
    () => () => {
      clearTimeout(toastTimer.current);
      clearInterval(running.current);
    },
    [],
  );
  useEffect(() => {
    const close = (e) => {
      if (!e.target.closest(".appearance-wrap")) setAppearance(false);
      if (!e.target.closest(".more-wrap")) setMenu(false);
    };
    document.addEventListener("pointerdown", close);
    return () => document.removeEventListener("pointerdown", close);
  }, []);
  useEffect(() => {
    const handler = (e) => {
      if (e.key === "Escape") {
        setAppearance(false);
        setMenu(false);
      }
      if (modal) return;
      const editing = ["INPUT", "TEXTAREA", "SELECT"].includes(
        document.activeElement?.tagName,
      );
      if (!editing && e.altKey && e.key === "ArrowLeft") {
        e.preventDefault();
        back();
      }
      if (!editing && e.altKey && e.key === "ArrowRight") {
        e.preventDefault();
        forward();
      }
      if (!editing && e.key === "Escape" && !appearance && !menu)
        setSelection([]);
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "f") {
        e.preventDefault();
        searchRef.current?.focus();
      }
      if (
        (e.ctrlKey || e.metaKey) &&
        e.key.toLowerCase() === "a" &&
        !["INPUT", "TEXTAREA", "SELECT"].includes(
          document.activeElement?.tagName,
        )
      ) {
        e.preventDefault();
        setSelection(visible.map((f) => f.id));
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [
    visible,
    modal,
    history,
    future,
    path,
    search,
    selection,
    anchor,
    sort,
    appearance,
    menu,
  ]);
  const changeSearch = (value) => {
    setSearch(value);
    setSelection([]);
    setAnchor(null);
  };
  const sortBy = (key) =>
    setSort((s) => ({ key, reverse: s.key === key ? !s.reverse : false }));
  const copyPath = async () => {
    try {
      await navigator.clipboard.writeText(
        `${archive}/${file.parent}${file.name}`,
      );
      notify("已复制文件路径");
    } catch {
      notify(`${archive}/${file.parent}${file.name}`);
    }
  };
  return (
    <div
      className={`app ${theme} ${compact ? "compact" : ""} ${inspector ? "" : "no-inspector"}`}
    >
      <header className="app-bar">
        <div className="brand">
          <Package size={25} weight="light" />
          <strong>AileArc</strong>
        </div>
        <div className="global-actions">
          <button onClick={() => setModal("open")}>
            <FolderOpen />
            打开
          </button>
          <button
            disabled={busy}
            onClick={() => {
              setSources([]);
              setModal("create");
            }}
          >
            <Plus />
            新建
          </button>
        </div>
        <span className="prototype-label">
          交互原型<span> / </span>示例数据
        </span>
        <div className="appearance-wrap">
          <button
            className={appearance ? "active" : ""}
            aria-expanded={appearance}
            onClick={() => {
              setAppearance(!appearance);
              setMenu(false);
            }}
          >
            <Palette />
            外观
            <CaretDown size={12} />
          </button>
          {appearance && (
            <section className="appearance popover" aria-label="外观设置">
              <div className="popover-title">选择配色</div>
              {[
                ["graphite", "暖石墨", "默认主题 · 香槟强调"],
                ["light", "浅色", "保留相同的布局与层次"],
              ].map(([id, name, desc]) => (
                <button
                  className="theme-choice"
                  key={id}
                  onClick={() => setTheme(id)}
                  aria-pressed={theme === id}
                >
                  <span className={`swatch ${id}`} />
                  <span>
                    {name}
                    <small>{desc}</small>
                  </span>
                  {theme === id && <Check size={17} />}
                </button>
              ))}
              <hr />
              <label className="setting">
                <Checkbox
                  checked={compact}
                  onChange={(e) => setCompact(e.target.checked)}
                />
                紧凑行高
              </label>
              <label className="setting">
                <Checkbox
                  checked={inspector}
                  onChange={(e) => setInspector(e.target.checked)}
                />
                显示详情栏
              </label>
              <button
                className="popover-close"
                onClick={() => setAppearance(false)}
              >
                完成
              </button>
            </section>
          )}
        </div>
      </header>
      <div className="workspace">
        <aside className="sidebar">
          <div className="side-label">当前压缩包</div>
          <div className="archive-tree">
            <IconButton
              label={tree ? "折叠目录树" : "展开目录树"}
              onClick={() => setTree(!tree)}
            >
              {tree ? <CaretDown size={13} /> : <CaretRight size={13} />}
            </IconButton>
            <button onClick={() => go("")}>
              <Archive size={19} />
              <span>{archive}</span>
            </button>
          </div>
          {tree && (
            <nav aria-label="压缩包目录">
              {["assets", "docs", "src", "config"].map((folder) => (
                <button
                  className={`tree-item ${path.startsWith(folder + "/") && !search ? "selected" : ""}`}
                  key={folder}
                  onClick={() => go(folder + "/")}
                >
                  <Folder
                    size={19}
                    weight={path === folder + "/" ? "fill" : "regular"}
                  />
                  <span>{folder}</span>
                  {folder === "docs" && <span className="count">12</span>}
                </button>
              ))}
              <button
                className="tree-item"
                onClick={() => {
                  go("");
                  setSelection(["README.md"]);
                }}
              >
                <FileText size={19} />
                README.md
              </button>
            </nav>
          )}
          <div className="sidebar-bottom">
            <div className="side-label">
              最近打开
              <ClockCounterClockwise size={14} />
            </div>
            {archives
              .filter((name) => name !== archive)
              .map((name) => (
                <button
                  className="recent"
                  key={name}
                  onClick={() => openArchive(name)}
                >
                  <Archive size={19} />
                  <span>
                    {name}
                    <small>10 月 20 日</small>
                  </span>
                </button>
              ))}
            <div className="side-divider" />
            <button className="workcopies" onClick={() => setModal("copies")}>
              <Copy size={18} />
              工作副本<span className="count">2</span>
            </button>
          </div>
        </aside>
        <main className="file-view">
          <div className="archive-heading">
            <div>
              <h1>{archive}</h1>
              <span className="archive-meta">
                {archive.endsWith(".7z") ? "7Z" : "ZIP"} 压缩包<span>·</span>
                18.6 MB
              </span>
            </div>
            <div className="search">
              <MagnifyingGlass size={18} />
              <input
                ref={searchRef}
                aria-label="搜索整个压缩包"
                placeholder="搜索整个压缩包"
                value={search}
                onChange={(e) => changeSearch(e.target.value)}
              />
              {search ? (
                <IconButton label="清除搜索" onClick={() => changeSearch("")}>
                  <X size={14} />
                </IconButton>
              ) : (
                <kbd>Ctrl F</kbd>
              )}
            </div>
          </div>
          <div className="location-bar">
            <div className="navigation">
              <IconButton
                label="后退"
                disabled={!history.length}
                onClick={back}
              >
                <ArrowLeft />
              </IconButton>
              <IconButton
                label="前进"
                disabled={!future.length}
                onClick={forward}
              >
                <ArrowRight />
              </IconButton>
              <IconButton
                label="上一级"
                disabled={!path && !search}
                onClick={() => {
                  const parts = path.split("/").filter(Boolean);
                  go(
                    parts.length > 1 ? parts.slice(0, -1).join("/") + "/" : "",
                  );
                }}
              >
                <ArrowUp />
              </IconButton>
            </div>
            <div className="breadcrumbs">
              <button onClick={() => go("")} title="压缩包根目录">
                {search ? "搜索结果" : archive}
              </button>
              {!search &&
                path
                  .split("/")
                  .filter(Boolean)
                  .map((part, i, parts) => (
                    <React.Fragment key={i}>
                      <CaretRight size={12} />
                      <button
                        onClick={() =>
                          go(parts.slice(0, i + 1).join("/") + "/")
                        }
                      >
                        {part}
                      </button>
                    </React.Fragment>
                  ))}
            </div>
            <div className="content-actions">
              <button
                className="primary"
                disabled={busy}
                onClick={() => showExtract(!selection.length)}
              >
                <ArrowUp size={16} />
                {selection.length ? "解压所选" : "解压全部"}
              </button>
              <IconButton
                label={inspector ? "隐藏详情" : "显示详情"}
                onClick={() => setInspector(!inspector)}
              >
                <SidebarSimple size={19} />
              </IconButton>
              <div className="more-wrap">
                <IconButton
                  label="更多操作"
                  aria-expanded={menu}
                  onClick={() => {
                    setMenu(!menu);
                    setAppearance(false);
                  }}
                >
                  <DotsThree size={23} />
                </IconButton>
                {menu && (
                  <div className="popover more">
                    <button disabled={busy} onClick={() => showExtract(true)}>
                      解压全部
                    </button>
                    <button
                      onClick={() => {
                        selectAll();
                        setMenu(false);
                      }}
                    >
                      {all ? "取消全选" : "选择全部可见项"}
                    </button>
                    <button
                      onClick={() => {
                        setSelection([]);
                        setMenu(false);
                      }}
                    >
                      清除选择
                    </button>
                    <button
                      onClick={() => {
                        setMenu(false);
                        setModal("shortcuts");
                      }}
                    >
                      键盘快捷键
                    </button>
                  </div>
                )}
              </div>
            </div>
          </div>
          <div className="table-scroll">
            <table>
              <thead>
                <tr>
                  <th className="check-cell">
                    <Checkbox
                      aria-label="选择全部可见项"
                      checked={all}
                      mixed={
                        !all && visible.some((f) => selection.includes(f.id))
                      }
                      onChange={selectAll}
                    />
                  </th>
                  {[
                    ["name", "名称"],
                    ["type", "类型"],
                    ["size", "大小"],
                    ["date", "修改时间"],
                  ].map(([key, title]) => (
                    <th
                      key={key}
                      className={`col-${key}`}
                      aria-sort={
                        sort.key === key
                          ? sort.reverse
                            ? "descending"
                            : "ascending"
                          : "none"
                      }
                    >
                      <button onClick={() => sortBy(key)}>
                        {title}
                        {sort.key === key &&
                          (sort.reverse ? (
                            <ArrowDown size={12} />
                          ) : (
                            <ArrowUp size={12} />
                          ))}
                      </button>
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {visible.map((f) => (
                  <tr
                    key={f.id}
                    ref={(node) => {
                      if (node) rowRefs.current.set(f.id, node);
                      else rowRefs.current.delete(f.id);
                    }}
                    className={selection.includes(f.id) ? "selected" : ""}
                    aria-selected={selection.includes(f.id)}
                    tabIndex={0}
                    onClick={(e) => toggle(f, e, true)}
                    onDoubleClick={() => openFile(f)}
                    onKeyDown={(e) => {
                      if (e.target !== e.currentTarget) return;
                      if (
                        ["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)
                      ) {
                        e.preventDefault();
                        const index = visible.findIndex(
                          (item) => item.id === f.id,
                        );
                        const nextIndex =
                          e.key === "Home"
                            ? 0
                            : e.key === "End"
                              ? visible.length - 1
                              : Math.max(
                                  0,
                                  Math.min(
                                    visible.length - 1,
                                    index + (e.key === "ArrowDown" ? 1 : -1),
                                  ),
                                );
                        const next = visible[nextIndex];
                        if (next) {
                          if (e.shiftKey) toggle(next, e, true);
                          else if (!e.ctrlKey && !e.metaKey) {
                            setSelection([next.id]);
                            setAnchor(next.id);
                          }
                          rowRefs.current.get(next.id)?.focus();
                        }
                      }
                      if (e.key === " ") {
                        e.preventDefault();
                        toggle(f, e);
                      }
                      if (e.key === "Enter") openFile(f);
                    }}
                  >
                    <td
                      className="check-cell"
                      onClick={(e) => e.stopPropagation()}
                      onDoubleClick={(e) => e.stopPropagation()}
                    >
                      <Checkbox
                        aria-label={`选择 ${f.name}`}
                        checked={selection.includes(f.id)}
                        onChange={() => {}}
                        onClick={(e) => toggle(f, e)}
                      />
                    </td>
                    <td className="filename">
                      <FileIcon file={f} />
                      <span>
                        {f.name}
                        {search && <small>{f.parent || "根目录"}</small>}
                      </span>
                    </td>
                    <td className="col-type">{typeName(f)}</td>
                    <td className="col-size">
                      {f.type === "folder" ? "—" : size(f.size)}
                    </td>
                    <td className="col-date">{f.date}</td>
                  </tr>
                ))}
              </tbody>
            </table>
            {!visible.length && (
              <div className="empty">
                <MagnifyingGlass size={30} />
                <h3>{search ? "没有找到匹配项" : "这个文件夹是空的"}</h3>
                <p>
                  {search
                    ? "试试其他名称，或清除搜索查看全部内容。"
                    : "你可以返回上一级继续浏览。"}
                </p>
                <button
                  onClick={() => (search ? changeSearch("") : go("docs/"))}
                >
                  {search ? "清除搜索" : "返回文档目录"}
                </button>
              </div>
            )}
          </div>
          {selection.length > 1 && (
            <div className="selection-strip">
              <span>
                <CheckCircle size={17} />
                已选择 {selection.length} 项
                <span className="muted">
                  · {size(selected.reduce((n, f) => n + f.size, 0))}
                </span>
              </span>
              <button onClick={() => setSelection([])}>取消选择</button>
            </div>
          )}
        </main>
        {inspector && (
          <aside className="inspector" aria-label="文件详情">
            <div className="inspector-label">
              {selected.length ? "所选项目" : "压缩包信息"}
              <IconButton
                label="关闭详情栏"
                onClick={() => setInspector(false)}
              >
                <X size={15} />
              </IconButton>
            </div>
            <div className="object-icon">
              {file ? (
                <FileIcon file={file} large />
              ) : selection.length > 1 ? (
                <Copy size={54} weight="light" />
              ) : (
                <Archive size={58} weight="light" />
              )}
            </div>
            <h2>
              {file?.name ||
                (selected.length ? `${selected.length} 个项目` : archive)}
            </h2>
            <p className="object-type">
              {file ? typeName(file) : selected.length ? "多项选择" : "压缩包"}
            </p>
            <div className="detail-heading">
              {file ? "文件属性" : "项目概览"}
            </div>
            <dl>
              <div>
                <dt>大小</dt>
                <dd>
                  {file?.type === "folder"
                    ? "—"
                    : selected.length
                      ? size(selected.reduce((n, f) => n + f.size, 0))
                      : "18.6 MB"}
                </dd>
              </div>
              {file ? (
                <>
                  <div>
                    <dt>位置</dt>
                    <dd className="path-value">{file.parent || "根目录"}</dd>
                  </div>
                  <div>
                    <dt>修改时间</dt>
                    <dd>{file.date}</dd>
                  </div>
                </>
              ) : (
                <div>
                  <dt>项目</dt>
                  <dd>{selected.length || files.length} 项</dd>
                </div>
              )}
            </dl>
            {file && (
              <>
                <button
                  className="outline open-default"
                  onClick={() => openFile(file)}
                >
                  {file.type === "folder" ? <FolderOpen /> : <ArrowSquareOut />}
                  {file.type === "folder" ? "打开文件夹" : "用默认应用打开"}
                </button>
                <button className="subtle copy-path" onClick={copyPath}>
                  <Copy size={15} />
                  复制路径
                </button>
              </>
            )}
            {selected.length > 1 && (
              <div className="selected-list">
                {selected.slice(0, 5).map((f) => (
                  <div key={f.id}>
                    <FileIcon file={f} />
                    <span>{f.name}</span>
                  </div>
                ))}
                {selected.length > 5 && <p>还有 {selected.length - 5} 项</p>}
              </div>
            )}
            <div className="inspector-note">
              <Info size={15} />
              <span>
                {file?.type === "folder"
                  ? "双击目录可进入；勾选后可解压整个文件夹。"
                  : "双击文件，通过默认应用打开。原压缩包保持不变。"}
              </span>
            </div>
          </aside>
        )}
      </div>
      <footer className="status-bar">
        <span>
          {visible.length} 个项目
          {selection.length > 0 && (
            <>
              {" "}
              <span className="separator">·</span> 已选择 {selection.length} 项
            </>
          )}
        </span>
        {job && (
          <div className="task-status">
            <span>
              {job.cancelled ? (
                <XCircle size={14} />
              ) : job.progress === 100 ? (
                <CheckCircle size={14} />
              ) : (
                <Archive size={14} />
              )}
              模拟{job.kind}
              {job.cancelled
                ? "已取消"
                : job.progress === 100
                  ? "完成"
                  : ` ${job.progress}%`}
            </span>
            <button onClick={() => setModal("job")}>查看任务</button>
            {busy ? (
              <>
                <progress value={job.progress} max="100" />
                <button
                  onClick={() => {
                    clearInterval(running.current);
                    setJob((current) => ({ ...current, cancelled: true }));
                    notify("模拟任务已取消");
                  }}
                >
                  取消
                </button>
              </>
            ) : (
              <IconButton label="清除任务状态" onClick={() => setJob(null)}>
                <X size={13} />
              </IconButton>
            )}
          </div>
        )}
        <label className="encoding">
          文件名编码
          <select
            aria-label="文件名编码"
            value={encoding}
            onChange={(e) => {
              setEncoding(e.target.value);
              notify(`原型演示：编码切换为 ${e.target.value}`);
            }}
          >
            {["自动", "UTF-8", "GBK", "Shift-JIS"].map((s) => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </label>
      </footer>
      {toast && (
        <div className="toast" role="status">
          <CheckCircle size={19} />
          {toast}
          <IconButton label="关闭提示" onClick={() => setToast("")}>
            <X size={14} />
          </IconButton>
        </div>
      )}
      {modal === "open" && (
        <Modal title="打开压缩包" onClose={() => setModal(null)}>
          <p className="dialog-description">
            选择一个示例，体验浏览和选择操作。原型不会读取你的本地文件。
          </p>
          <div className="archive-choices">
            {archives.map((name) => (
              <button key={name} onClick={() => openArchive(name)}>
                <Archive size={25} />
                <span>
                  {name}
                  <small>演示内容 · 18.6 MB</small>
                </span>
                <CaretRight />
              </button>
            ))}
          </div>
        </Modal>
      )}
      {modal === "extract" && (
        <Modal title="解压内容" onClose={() => setModal(null)}>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              if (!destinationError(destination))
                runJob("解压", extraction.length, destination.trim());
            }}
          >
            <p className="dialog-description">
              {extraction.length} 个项目 · {archive}
            </p>
            <label className="field">
              目标文件夹
              <input
                required
                aria-invalid={!!destinationError(destination)}
                aria-describedby={
                  destinationError(destination)
                    ? "destination-error"
                    : undefined
                }
                value={destination}
                onChange={(e) => setDestination(e.target.value)}
              />
            </label>
            {destinationError(destination) && (
              <p className="field-error" id="destination-error" role="alert">
                {destinationError(destination)}
              </p>
            )}
            <label className="setting">
              <Checkbox
                checked={keepPaths}
                onChange={(e) => setKeepPaths(e.target.checked)}
              />
              保留包内目录结构
            </label>
            <label className="setting">
              <Checkbox
                checked={openAfter}
                onChange={(e) => setOpenAfter(e.target.checked)}
              />
              完成后打开文件夹
            </label>
            <label className="field">
              遇到同名文件
              <select
                value={conflict}
                onChange={(e) => setConflict(e.target.value)}
              >
                <option value="ask">询问我</option>
                <option value="rename">自动重命名</option>
                <option value="skip">跳过</option>
              </select>
            </label>
            <div className="destination-preview">
              <small>文件将保存到</small>
              {previewTargets.slice(0, 3).map((target, i) => (
                <span className="target-path" key={i} title={target}>
                  {target}
                </span>
              ))}
              {previewTargets.length > 3 && (
                <small>还有 {previewTargets.length - 3} 个项目</small>
              )}
            </div>
            <p className="simulation-note">
              仅模拟交互，不会向此位置写入文件。
            </p>
            <div className="dialog-actions">
              <button type="button" onClick={() => setModal(null)}>
                取消
              </button>
              <button
                className="primary"
                type="submit"
                disabled={!!destinationError(destination) || busy}
              >
                开始解压
              </button>
            </div>
          </form>
        </Modal>
      )}
      {modal === "create" && (
        <Modal title="新建压缩包" onClose={() => setModal(null)}>
          <form
            onSubmit={(e) => {
              e.preventDefault();
              if (!nameError(createName) && sources.length)
                runJob("创建", sources.length, outputName);
            }}
          >
            <p className="dialog-description">用示例文件体验创建流程。</p>
            <label className="field">
              压缩包名称
              <div className="input-suffix">
                <input
                  required
                  aria-label="压缩包名称"
                  aria-invalid={!!nameError(createName)}
                  aria-describedby={
                    nameError(createName) ? "name-error" : "output-name"
                  }
                  value={createName}
                  onChange={(e) => setCreateName(e.target.value)}
                />
              </div>
            </label>
            {nameError(createName) ? (
              <p className="field-error" id="name-error" role="alert">
                {nameError(createName)}
              </p>
            ) : (
              <p className="output-name" id="output-name">
                将创建 <strong>{outputName}</strong>
              </p>
            )}
            <label className="field">
              格式
              <select
                value={format}
                onChange={(e) => setFormat(e.target.value)}
              >
                <option value="zip">ZIP · 兼容性更好</option>
                <option value="7z">7Z · 压缩率更高</option>
              </select>
            </label>
            <div className="source-list">
              {sources.length ? (
                sources.map((s) => (
                  <div key={s}>
                    <FileText size={18} />
                    {s}
                    <button
                      type="button"
                      className="icon-button"
                      aria-label={`移除 ${s}`}
                      onClick={() =>
                        setSources((current) =>
                          current.filter((item) => item !== s),
                        )
                      }
                    >
                      <X size={14} />
                    </button>
                  </div>
                ))
              ) : (
                <p>还没有选择文件</p>
              )}
            </div>
            <button
              type="button"
              className="outline"
              onClick={() => setSources(["README.md", "config.yml", "assets"])}
            >
              <Plus />
              添加示例文件
            </button>
            <p className="simulation-note">
              只演示操作过程，不生成真实压缩包。
            </p>
            <div className="dialog-actions">
              <button type="button" onClick={() => setModal(null)}>
                取消
              </button>
              <button
                className="primary"
                disabled={!sources.length || !!nameError(createName) || busy}
                type="submit"
              >
                创建压缩包
              </button>
            </div>
          </form>
        </Modal>
      )}
      {modal === "job" && job && (
        <Modal title="模拟任务" onClose={() => setModal(null)}>
          <div className="job-summary">
            {job.cancelled ? (
              <XCircle size={30} />
            ) : job.progress === 100 ? (
              <CheckCircle size={30} />
            ) : (
              <Archive size={30} />
            )}
            <h3>
              {job.kind}
              {job.cancelled
                ? "已取消"
                : job.progress === 100
                  ? "已完成"
                  : "进行中"}
            </h3>
            <p>{job.count} 个项目</p>
          </div>
          <div className="destination-preview">
            <small>{job.kind === "创建" ? "输出文件名" : "目标位置"}</small>
            <span className="target-path">{job.context}</span>
          </div>
          {busy && (
            <progress className="job-progress" value={job.progress} max="100" />
          )}
          {job.openAfter && job.progress === 100 && !job.cancelled && (
            <p className="simulation-note">
              正式版本将在完成后打开目标文件夹。
            </p>
          )}
          <p className="simulation-note">
            这里是交互演示，没有创建、修改或删除任何真实文件。
          </p>
          <div className="dialog-actions">
            <button onClick={() => setModal(null)}>返回浏览</button>
          </div>
        </Modal>
      )}
      {modal === "shortcuts" && (
        <Modal title="键盘快捷键" onClose={() => setModal(null)}>
          <dl className="shortcut-list">
            {[
              ["搜索压缩包", "Ctrl + F"],
              ["选择全部可见项", "Ctrl + A"],
              ["移动选择", "↑ / ↓ · Home / End"],
              ["连续选择", "Shift + ↑ / ↓"],
              ["进入目录 / 打开文件", "Enter"],
              ["切换当前项选择", "Space"],
              ["后退 / 前进", "Alt + ← / →"],
              ["关闭对话框 / 清除选择", "Esc"],
            ].map(([action, key]) => (
              <div key={action}>
                <dt>{action}</dt>
                <dd>
                  <kbd>{key}</kbd>
                </dd>
              </div>
            ))}
          </dl>
        </Modal>
      )}
      {modal === "copies" && (
        <Modal title="工作副本" onClose={() => setModal(null)}>
          <p className="dialog-description">
            以下为示例状态，用于体验外部打开后的文件管理。
          </p>
          {[
            ["config.yml", "已修改"],
            ["Getting Started.pdf", "未修改"],
          ].map(([name, state]) => (
            <div className="copy-row" key={name}>
              <FileText size={22} />
              <span>
                {name}
                <small className={state === "已修改" ? "accent" : ""}>
                  {state}
                </small>
              </span>
              <button onClick={() => notify(`原型演示：另存 ${name}`)}>
                另存为
              </button>
            </div>
          ))}
          <p className="simulation-note">
            修改的工作副本会保留，不会自动回写原压缩包。
          </p>
        </Modal>
      )}
    </div>
  );
}
