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
    static let fontAwesomeTextAttribute = [
        NSAttributedString.Key.font: UIFont(name: "Font Awesome 6 Free Solid", size: 17)!
    ]
    required init?(coder aDecoder: NSCoder) {
        super.init(coder: aDecoder)
        
        self.setTitleTextAttributes(AwesomeBarButtonItem.fontAwesomeTextAttribute, for: .normal)
        self.setTitleTextAttributes(AwesomeBarButtonItem.fontAwesomeTextAttribute, for: .highlighted)
        self.setTitleTextAttributes(AwesomeBarButtonItem.fontAwesomeTextAttribute, for: .selected)
        self.setTitleTextAttributes(AwesomeBarButtonItem.fontAwesomeTextAttribute, for: .focused)
        self.setTitleTextAttributes(AwesomeBarButtonItem.fontAwesomeTextAttribute, for: .disabled)
    }
}
