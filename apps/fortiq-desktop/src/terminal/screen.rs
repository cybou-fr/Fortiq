#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TerminalCell {
    pub c: char,
    pub fg: u32,
    pub bg: u32,
    pub bold: bool,
    pub italic: bool,
    pub underline: bool,
}

impl Default for TerminalCell {
    fn default() -> Self {
        Self {
            c: ' ',
            fg: 0xFFCCCCCC, // Light grey default
            bg: 0xFF121212, // Dark background default
            bold: false,
            italic: false,
            underline: false,
        }
    }
}

#[derive(Debug, Clone)]
pub struct TerminalScreen {
    pub cols: u16,
    pub rows: u16,
    pub cursor_row: u16,
    pub cursor_col: u16,
    pub cursor_visible: bool,
    pub use_alternate: bool,

    // Active attributes
    pub cur_fg: u32,
    pub cur_bg: u32,
    pub cur_bold: bool,
    pub cur_italic: bool,
    pub cur_underline: bool,

    primary_buffer: Vec<Vec<TerminalCell>>,
    alternate_buffer: Vec<Vec<TerminalCell>>,
    saved_cursor: (u16, u16),
}

impl TerminalScreen {
    pub fn new(cols: u16, rows: u16) -> Self {
        let default_cell = TerminalCell::default();
        let cols_u = cols as usize;
        let rows_u = rows as usize;
        let primary = vec![vec![default_cell; cols_u]; rows_u];
        let alternate = vec![vec![default_cell; cols_u]; rows_u];

        Self {
            cols,
            rows,
            cursor_row: 0,
            cursor_col: 0,
            cursor_visible: true,
            use_alternate: false,
            cur_fg: 0xFFCCCCCC,
            cur_bg: 0xFF121212,
            cur_bold: false,
            cur_italic: false,
            cur_underline: false,
            primary_buffer: primary,
            alternate_buffer: alternate,
            saved_cursor: (0, 0),
        }
    }

    pub fn resize(&mut self, cols: u16, rows: u16) {
        if cols == 0 || rows == 0 {
            return;
        }
        self.cols = cols;
        self.rows = rows;
        let cols_u = cols as usize;
        let rows_u = rows as usize;
        let default_cell = TerminalCell::default();

        let resize_buffer = |buf: &mut Vec<Vec<TerminalCell>>| {
            buf.resize(rows_u, vec![default_cell; cols_u]);
            for row in buf.iter_mut() {
                row.resize(cols_u, default_cell);
            }
        };

        resize_buffer(&mut self.primary_buffer);
        resize_buffer(&mut self.alternate_buffer);

        if self.cursor_row >= rows {
            self.cursor_row = rows.saturating_sub(1);
        }
        if self.cursor_col >= cols {
            self.cursor_col = cols.saturating_sub(1);
        }
    }

    fn active_buffer_mut(&mut self) -> &mut Vec<Vec<TerminalCell>> {
        if self.use_alternate {
            &mut self.alternate_buffer
        } else {
            &mut self.primary_buffer
        }
    }

    pub fn active_buffer(&self) -> &Vec<Vec<TerminalCell>> {
        if self.use_alternate {
            &self.alternate_buffer
        } else {
            &self.primary_buffer
        }
    }

    pub fn put_char(&mut self, c: char) {
        if self.cursor_col >= self.cols {
            self.new_line();
        }

        let r = self.cursor_row as usize;
        let c_idx = self.cursor_col as usize;
        let cell = TerminalCell {
            c,
            fg: self.cur_fg,
            bg: self.cur_bg,
            bold: self.cur_bold,
            italic: self.cur_italic,
            underline: self.cur_underline,
        };

        let buf = self.active_buffer_mut();
        if r < buf.len() && c_idx < buf[r].len() {
            buf[r][c_idx] = cell;
        }

        self.cursor_col = (self.cursor_col + 1).min(self.cols);
    }

    pub fn new_line(&mut self) {
        self.cursor_col = 0;
        if self.cursor_row + 1 >= self.rows {
            self.scroll_up(1);
        } else {
            self.cursor_row += 1;
        }
    }

    pub fn carriage_return(&mut self) {
        self.cursor_col = 0;
    }

    pub fn backspace(&mut self) {
        if self.cursor_col > 0 {
            self.cursor_col -= 1;
        }
    }

    pub fn tab(&mut self) {
        let next_tab = (self.cursor_col / 8 + 1) * 8;
        self.cursor_col = next_tab.min(self.cols.saturating_sub(1));
    }

    pub fn scroll_up(&mut self, count: usize) {
        let default_cell = TerminalCell::default();
        let cols_u = self.cols as usize;
        let buf = self.active_buffer_mut();
        for _ in 0..count {
            if !buf.is_empty() {
                buf.remove(0);
                buf.push(vec![default_cell; cols_u]);
            }
        }
    }

    pub fn clear_screen(&mut self) {
        let default_cell = TerminalCell::default();
        let buf = self.active_buffer_mut();
        for row in buf.iter_mut() {
            row.fill(default_cell);
        }
        self.cursor_row = 0;
        self.cursor_col = 0;
    }

    pub fn clear_line(&mut self, mode: u8) {
        let r = self.cursor_row as usize;
        let c = self.cursor_col as usize;
        let default_cell = TerminalCell::default();
        let buf = self.active_buffer_mut();
        if r >= buf.len() {
            return;
        }
        let row = &mut buf[r];
        match mode {
            0 => {
                // cursor to end
                for cell in row.iter_mut().skip(c) {
                    *cell = default_cell;
                }
            }
            1 => {
                // start to cursor
                for cell in row.iter_mut().take(c + 1) {
                    *cell = default_cell;
                }
            }
            2 => {
                // entire line
                row.fill(default_cell);
            }
            _ => {}
        }
    }

    pub fn clear_display(&mut self, mode: u8) {
        let default_cell = TerminalCell::default();
        let r = self.cursor_row as usize;
        let c = self.cursor_col as usize;
        let buf = self.active_buffer_mut();
        match mode {
            0 => {
                // cursor to end of screen
                if r < buf.len() {
                    for cell in buf[r].iter_mut().skip(c) {
                        *cell = default_cell;
                    }
                }
                for row in buf.iter_mut().skip(r + 1) {
                    row.fill(default_cell);
                }
            }
            1 => {
                // top of screen to cursor
                for row in buf.iter_mut().take(r) {
                    row.fill(default_cell);
                }
                if r < buf.len() {
                    for cell in buf[r].iter_mut().take(c + 1) {
                        *cell = default_cell;
                    }
                }
            }
            2 | 3 => {
                // entire screen
                for row in buf.iter_mut() {
                    row.fill(default_cell);
                }
            }
            _ => {}
        }
    }

    pub fn set_cursor_pos(&mut self, row: u16, col: u16) {
        self.cursor_row = row.min(self.rows.saturating_sub(1));
        self.cursor_col = col.min(self.cols.saturating_sub(1));
    }

    pub fn move_cursor(&mut self, d_row: i16, d_col: i16) {
        let new_r = (self.cursor_row as i16 + d_row).clamp(0, self.rows.saturating_sub(1) as i16);
        let new_c = (self.cursor_col as i16 + d_col).clamp(0, self.cols.saturating_sub(1) as i16);
        self.cursor_row = new_r as u16;
        self.cursor_col = new_c as u16;
    }

    pub fn save_cursor(&mut self) {
        self.saved_cursor = (self.cursor_row, self.cursor_col);
    }

    pub fn restore_cursor(&mut self) {
        self.cursor_row = self.saved_cursor.0.min(self.rows.saturating_sub(1));
        self.cursor_col = self.saved_cursor.1.min(self.cols.saturating_sub(1));
    }

    pub fn render_plain_text(&self) -> String {
        let buf = self.active_buffer();
        let mut out = String::new();
        for row in buf {
            let s: String = row.iter().map(|c| c.c).collect();
            out.push_str(s.trim_end());
            out.push('\n');
        }
        out
    }
}
