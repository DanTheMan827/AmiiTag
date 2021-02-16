//
//  awesomeBarItem.swift
//  Bingo
//
//  Created by Daniel Radtke on 9/27/15.
//  Copyright © 2015 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit

@IBDesignable
class AwesomeBarButtonItem: UIBarButtonItem {
    required init?(coder aDecoder: NSCoder) {
        super.init(coder: aDecoder)
        let fontAwesomeTextAttribute = [
            NSAttributedString.Key.font: UIFont(name: "Font Awesome 5 Free Solid", size: 17)!
        ]
        
        self.setTitleTextAttributes(fontAwesomeTextAttribute, for: .normal)
        self.setTitleTextAttributes(fontAwesomeTextAttribute, for: .highlighted)
        self.setTitleTextAttributes(fontAwesomeTextAttribute, for: .selected)
        self.setTitleTextAttributes(fontAwesomeTextAttribute, for: .focused)
        self.setTitleTextAttributes(fontAwesomeTextAttribute, for: .disabled)
    }
}
