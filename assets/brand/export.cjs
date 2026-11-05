// Run from the repository: npm install --prefix .artifacts/brand-tools @resvg/resvg-js@2.6.2
// Then: node assets/brand/export.cjs
const fs = require("node:fs");
const path = require("node:path");
const {Resvg} = require("../../.artifacts/brand-tools/node_modules/@resvg/resvg-js");
const file = name => path.join(__dirname,name);
const render = (name,width) => new Resvg(fs.readFileSync(file(name+".svg")), {fitTo:{mode:"width",value:width}}).render().asPng();
for (const [name,width] of [["banner",1280],["social",1200],["mark",512],["lockup-dark",608],["lockup-light",608]]) {
  fs.writeFileSync(file(name+".png"),render(name,width));
}
fs.writeFileSync(file("package-150.png"),render("mark",150));
const sizes=[16,20,24,32,48,64,128,256], frames=sizes.map(n=>render("mark",n));
const head=Buffer.alloc(6+16*sizes.length);
head.writeUInt16LE(1,2); head.writeUInt16LE(sizes.length,4);
let offset=head.length;
frames.forEach((frame,i)=>{
  const pos=6+i*16; head[pos]=head[pos+1]=sizes[i]%256;
  head.writeUInt16LE(1,pos+4);head.writeUInt16LE(32,pos+6);
  head.writeUInt32LE(frame.length,pos+8);head.writeUInt32LE(offset,pos+12);offset+=frame.length;
});
fs.writeFileSync(file("AileArc.ico"),Buffer.concat([head,...frames]));
