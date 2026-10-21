const make = (parent, rows) =>
  rows.map(([name, type, size], i) => ({
    id: parent + name,
    name,
    type,
    size,
    parent,
    date: `2026-10-${String(20 - (i % 5)).padStart(2, "0")} 14:32`,
  }));
export const archives = [
  "project-kit.zip",
  "design-resources.zip",
  "website-build.7z",
];
export const files = [
  ...make("", [
    ["assets", "folder", 0],
    ["docs", "folder", 0],
    ["src", "folder", 0],
    ["config", "folder", 0],
    ["README.md", "md", 5],
  ]),
  ...make("docs/", [
    ["Examples", "folder", 0],
    ["Changelog.md", "md", 14],
    ["Configuration.md", "md", 8],
    ["Deployment.pdf", "pdf", 320],
    ["FAQ.md", "md", 9],
    ["Getting Started.pdf", "pdf", 248],
    ["Installation.md", "md", 12],
    ["Licensing.md", "md", 7],
    ["Migration.md", "md", 11],
    ["README.md", "md", 5],
    ["Shortcuts.md", "md", 6],
    ["Troubleshooting.md", "md", 10],
  ]),
  ...make("docs/Examples/", [
    ["example.yml", "code", 4],
    ["sample.json", "code", 2],
  ]),
  ...make("assets/", [
    ["app-icon.svg", "code", 8],
    ["cover.png", "image", 1024],
    ["fonts", "folder", 0],
  ]),
  ...make("src/", [
    ["App.cs", "code", 12],
    ["MainWindow.cs", "code", 32],
  ]),
  ...make("config/", [
    ["config.yml", "code", 3],
    ["settings.json", "code", 2],
  ]),
];
export const typeName = (f) =>
  ({
    folder: "文件夹",
    md: "Markdown",
    pdf: "PDF 文档",
    code: "配置与代码",
    image: "图片",
  })[f.type];
export const size = (n) =>
  n >= 1024 ? `${(n / 1024).toFixed(1)} MB` : `${n} KB`;
