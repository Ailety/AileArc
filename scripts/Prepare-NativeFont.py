"""Build an unmodified-coverage static instance of the pinned OFL CJK font."""
import sys
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

font = TTFont(sys.argv[1])
instantiateVariableFont(font, {"wght": 400}, inplace=True)
font.save(sys.argv[2])
