use super::screen::TerminalScreen;
use vte::{Params, Perform};

pub struct TerminalPerformer<'a> {
    pub screen: &'a mut TerminalScreen,
}

impl<'a> TerminalPerformer<'a> {
    pub fn new(screen: &'a mut TerminalScreen) -> Self {
        Self { screen }
    }

    fn handle_sgr(&mut self, params: &Params) {
        for sub in params.iter() {
            let code = sub[0];
            match code {
                0 => {
                    // Reset
                    self.screen.cur_fg = 0xFFCCCCCC;
                    self.screen.cur_bg = 0xFF121212;
                    self.screen.cur_bold = false;
                    self.screen.cur_italic = false;
                    self.screen.cur_underline = false;
                }
                1 => self.screen.cur_bold = true,
                3 => self.screen.cur_italic = true,
                4 => self.screen.cur_underline = true,
                22 => self.screen.cur_bold = false,
                23 => self.screen.cur_italic = false,
                24 => self.screen.cur_underline = false,
                // Standard foregrounds 30-37
                30 => self.screen.cur_fg = 0xFF000000,
                31 => self.screen.cur_fg = 0xFFE5484D, // Red
                32 => self.screen.cur_fg = 0xFF16A36D, // Green
                33 => self.screen.cur_fg = 0xFFEAA612, // Yellow
                34 => self.screen.cur_fg = 0xFF0B84FF, // Blue
                35 => self.screen.cur_fg = 0xFFA05CE0, // Magenta
                36 => self.screen.cur_fg = 0xFF17B897, // Cyan
                37 => self.screen.cur_fg = 0xFFCCCCCC, // White
                39 => self.screen.cur_fg = 0xFFCCCCCC, // Default fg
                // Standard backgrounds 40-47
                40 => self.screen.cur_bg = 0xFF121212,
                41 => self.screen.cur_bg = 0xFF701A1E,
                42 => self.screen.cur_bg = 0xFF0D5E3E,
                43 => self.screen.cur_bg = 0xFF7D570A,
                44 => self.screen.cur_bg = 0xFF064789,
                45 => self.screen.cur_bg = 0xFF532F73,
                46 => self.screen.cur_bg = 0xFF0D5C4C,
                47 => self.screen.cur_bg = 0xFF4A4A4A,
                49 => self.screen.cur_bg = 0xFF121212, // Default bg
                // High intensity foregrounds 90-97
                90 => self.screen.cur_fg = 0xFF707070,
                91 => self.screen.cur_fg = 0xFFFF6369,
                92 => self.screen.cur_fg = 0xFF2CD996,
                93 => self.screen.cur_fg = 0xFFFFC53D,
                94 => self.screen.cur_fg = 0xFF52A8FF,
                95 => self.screen.cur_fg = 0xFFBF7AF0,
                96 => self.screen.cur_fg = 0xFF35E0BD,
                97 => self.screen.cur_fg = 0xFFFFFFFF,
                // High intensity backgrounds 100-107
                100 => self.screen.cur_bg = 0xFF2B2B2B,
                101 => self.screen.cur_bg = 0xFF8E2126,
                102 => self.screen.cur_bg = 0xFF127B52,
                103 => self.screen.cur_bg = 0xFFA3720C,
                104 => self.screen.cur_bg = 0xFF0A58A8,
                105 => self.screen.cur_bg = 0xFF6C3D96,
                106 => self.screen.cur_bg = 0xFF127863,
                107 => self.screen.cur_bg = 0xFF6E6E6E,
                _ => {}
            }
        }
    }
}

impl<'a> Perform for TerminalPerformer<'a> {
    fn print(&mut self, c: char) {
        self.screen.put_char(c);
    }

    fn execute(&mut self, byte: u8) {
        match byte {
            b'\n' => self.screen.new_line(),
            b'\r' => self.screen.carriage_return(),
            b'\x08' => self.screen.backspace(),
            b'\t' => self.screen.tab(),
            _ => {}
        }
    }

    fn hook(&mut self, _params: &Params, _intermediates: &[u8], _ignore: bool, _action: char) {}

    fn put(&mut self, _byte: u8) {}

    fn unhook(&mut self) {}

    fn osc_dispatch(&mut self, _params: &[&[u8]], _bell_terminated: bool) {}

    fn csi_dispatch(&mut self, params: &Params, intermediates: &[u8], _ignore: bool, action: char) {
        let is_private = intermediates.contains(&b'?');
        match action {
            'A' => {
                // Cursor Up
                let n = params.iter().next().map(|p| p[0]).unwrap_or(1).max(1);
                self.screen.move_cursor(-(n as i16), 0);
            }
            'B' => {
                // Cursor Down
                let n = params.iter().next().map(|p| p[0]).unwrap_or(1).max(1);
                self.screen.move_cursor(n as i16, 0);
            }
            'C' => {
                // Cursor Forward
                let n = params.iter().next().map(|p| p[0]).unwrap_or(1).max(1);
                self.screen.move_cursor(0, n as i16);
            }
            'D' => {
                // Cursor Backward
                let n = params.iter().next().map(|p| p[0]).unwrap_or(1).max(1);
                self.screen.move_cursor(0, -(n as i16));
            }
            'H' | 'f' => {
                // Cursor Position (row, col) 1-based
                let mut iter = params.iter();
                let row = iter
                    .next()
                    .map(|p| p[0])
                    .unwrap_or(1)
                    .max(1)
                    .saturating_sub(1);
                let col = iter
                    .next()
                    .map(|p| p[0])
                    .unwrap_or(1)
                    .max(1)
                    .saturating_sub(1);
                self.screen.set_cursor_pos(row, col);
            }
            'J' => {
                // Erase in Display
                let mode = params.iter().next().map(|p| p[0] as u8).unwrap_or(0);
                self.screen.clear_display(mode);
            }
            'K' => {
                // Erase in Line
                let mode = params.iter().next().map(|p| p[0] as u8).unwrap_or(0);
                self.screen.clear_line(mode);
            }
            'm' => {
                // Select Graphic Rendition
                self.handle_sgr(params);
            }
            'h' => {
                // Set Mode
                if is_private {
                    for param in params.iter() {
                        match param[0] {
                            25 => self.screen.cursor_visible = true,
                            1049 => {
                                self.screen.use_alternate = true;
                                self.screen.clear_screen();
                            }
                            _ => {}
                        }
                    }
                }
            }
            'l' => {
                // Reset Mode
                if is_private {
                    for param in params.iter() {
                        match param[0] {
                            25 => self.screen.cursor_visible = false,
                            1049 => self.screen.use_alternate = false,
                            _ => {}
                        }
                    }
                }
            }
            's' => self.screen.save_cursor(),
            'u' => self.screen.restore_cursor(),
            _ => {}
        }
    }

    fn esc_dispatch(&mut self, _intermediates: &[u8], _ignore: bool, byte: u8) {
        match byte {
            b'7' => self.screen.save_cursor(),
            b'8' => self.screen.restore_cursor(),
            b'c' => self.screen.clear_screen(),
            _ => {}
        }
    }
}
